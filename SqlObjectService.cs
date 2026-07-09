using Microsoft.Data.SqlClient;
using System.Text;

/// <summary>A database object found by name: id, type ('U','V','P','FN','IF','TF'), schema, name.</summary>
sealed record DbObject(int Id, string Type, string Schema, string Name);

/// <summary>
/// Connects to the server/database of the active SSMS window (Windows auth by
/// default; per-server overrides in %APPDATA%\SsmsSnippetExpander\connections.json),
/// resolves an identifier to an object and scripts its CREATE.
/// </summary>
internal static class SqlObjectService
{
    static Dictionary<string, string>? _overrides;

    static string BuildConnStr(string server, string db)
    {
        _overrides ??= LoadOverrides();
        if (_overrides.TryGetValue(server, out var custom))
            return new SqlConnectionStringBuilder(custom) { InitialCatalog = db }.ConnectionString;

        return new SqlConnectionStringBuilder
        {
            DataSource             = server,
            InitialCatalog         = db,
            IntegratedSecurity     = true,
            TrustServerCertificate = true,
            ConnectTimeout         = 8,
            ApplicationName        = "SsmsSnippetExpander",
        }.ConnectionString;
    }

    // Optional: { "SERVERNAME": "full connection string", ... } for SQL logins etc.
    static Dictionary<string, string> LoadOverrides()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SsmsSnippetExpander", "connections.json");
            if (File.Exists(path))
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (parsed != null)
                    foreach (var (k, v) in parsed) map[k] = v;
            }
        }
        catch { /* bad config — fall back to integrated auth */ }
        return map;
    }

    public static DbObject? Resolve(string server, string db, string name)
    {
        using var cn = new SqlConnection(BuildConnStr(server, db));
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT TOP (1) o.object_id, RTRIM(o.[type]), s.name, o.name
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.name = @name AND o.[type] IN ('U','V','P','FN','IF','TF')
ORDER BY CASE WHEN s.name = 'dbo' THEN 0 ELSE 1 END, s.name;";
        cmd.Parameters.AddWithValue("@name", name);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new DbObject(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3))
            : null;
    }

    public static string Script(string server, string db, DbObject obj)
    {
        using var cn = new SqlConnection(BuildConnStr(server, db));
        cn.Open();

        string body = obj.Type == "U" ? ScriptTable(cn, obj) : ScriptModule(cn, obj);

        var sb = new StringBuilder();
        sb.AppendLine($"-- {obj.Schema}.{obj.Name}  (scripted from {server}.{db} by SSMS Snippet Expander)");
        sb.AppendLine($"USE [{db}];");
        sb.AppendLine("GO");
        sb.AppendLine();
        sb.Append(body);
        return sb.ToString();
    }

    // ── Views / procedures / functions: the stored definition ─────────────────
    static string ScriptModule(SqlConnection cn, DbObject obj)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT definition, uses_ansi_nulls, uses_quoted_identifier FROM sys.sql_modules WHERE object_id = @id;";
        cmd.Parameters.AddWithValue("@id", obj.Id);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0))
            throw new InvalidOperationException(
                $"No definition available for {obj.Schema}.{obj.Name} (encrypted, or missing VIEW DEFINITION permission).");

        bool ansiNulls  = r.IsDBNull(1) || r.GetBoolean(1);
        bool quotedIdent = r.IsDBNull(2) || r.GetBoolean(2);

        var sb = new StringBuilder();
        sb.AppendLine($"SET ANSI_NULLS {(ansiNulls ? "ON" : "OFF")};");
        sb.AppendLine("GO");
        sb.AppendLine($"SET QUOTED_IDENTIFIER {(quotedIdent ? "ON" : "OFF")};");
        sb.AppendLine("GO");
        sb.AppendLine();
        sb.AppendLine(r.GetString(0).Trim());
        sb.AppendLine("GO");
        return sb.ToString();
    }

    // ── Tables: rebuild CREATE TABLE from the catalog views ───────────────────
    static string ScriptTable(SqlConnection cn, DbObject obj)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = TableSql;
        cmd.Parameters.AddWithValue("@id", obj.Id);

        var columns     = new List<string>();
        var constraints = new List<string>();
        var postScripts = new List<string>();
        string? dbCollation = null;

        using (var r = cmd.ExecuteReader())
        {
            if (r.Read() && !r.IsDBNull(0)) dbCollation = r.GetString(0);

            r.NextResult(); // columns
            while (r.Read()) columns.Add(FormatColumn(r, dbCollation));

            r.NextResult(); // PK / UNIQUE constraints
            while (r.Read())
            {
                string kind      = r.GetString(1) == "PK" ? "PRIMARY KEY" : "UNIQUE";
                string clustered = r.GetByte(2) == 1 ? "CLUSTERED" : "NONCLUSTERED";
                constraints.Add($"CONSTRAINT {Q(r.GetString(0))} {kind} {clustered} ({r.GetString(3)})");
            }

            r.NextResult(); // foreign keys
            while (r.Read())
            {
                var fk = new StringBuilder(
                    $"CONSTRAINT {Q(r.GetString(0))} FOREIGN KEY ({r.GetString(2)}) REFERENCES {r.GetString(1)} ({r.GetString(3)})");
                string del = r.GetString(4), upd = r.GetString(5);
                if (del != "NO_ACTION") fk.Append(" ON DELETE ").Append(del.Replace('_', ' '));
                if (upd != "NO_ACTION") fk.Append(" ON UPDATE ").Append(upd.Replace('_', ' '));
                constraints.Add(fk.ToString());
            }

            r.NextResult(); // non-constraint indexes
            while (r.Read())
            {
                var ix = new StringBuilder("CREATE ");
                if (r.GetBoolean(2)) ix.Append("UNIQUE ");
                ix.Append(r.GetString(1) == "CLUSTERED" ? "CLUSTERED" : "NONCLUSTERED");
                ix.Append($" INDEX {Q(r.GetString(0))} ON {Q(obj.Schema)}.{Q(obj.Name)} ({r.GetString(3)})");
                if (!r.IsDBNull(4) && r.GetString(4).Length > 0) ix.Append($" INCLUDE ({r.GetString(4)})");
                if (!r.IsDBNull(5)) ix.Append($" WHERE {r.GetString(5)}");
                ix.Append(';');
                postScripts.Add(ix.ToString());
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE {Q(obj.Schema)}.{Q(obj.Name)}");
        sb.AppendLine("(");
        var lines = columns.Concat(constraints).ToList();
        for (int i = 0; i < lines.Count; i++)
            sb.AppendLine($"    {lines[i]}{(i < lines.Count - 1 ? "," : "")}");
        sb.AppendLine(");");
        sb.AppendLine("GO");
        foreach (var p in postScripts)
        {
            sb.AppendLine();
            sb.AppendLine(p);
            sb.AppendLine("GO");
        }
        return sb.ToString();
    }

    static string FormatColumn(SqlDataReader r, string? dbCollation)
    {
        string name = Q(r.GetString(0));

        if (r.GetBoolean(9)) // computed
        {
            string def = r.IsDBNull(10) ? "" : r.GetString(10);
            return $"{name} AS {def}{(r.GetBoolean(11) ? " PERSISTED" : "")}";
        }

        var sb = new StringBuilder(name)
            .Append(' ')
            .Append(FormatType(r.GetString(1), r.GetInt16(2), r.GetByte(3), r.GetByte(4)));

        string? collation = r.IsDBNull(12) ? null : r.GetString(12);
        if (collation != null && !collation.Equals(dbCollation, StringComparison.OrdinalIgnoreCase))
            sb.Append(" COLLATE ").Append(collation);

        if (r.GetBoolean(6)) sb.Append($" IDENTITY({r.GetInt64(7)},{r.GetInt64(8)})");
        sb.Append(r.GetBoolean(5) ? " NULL" : " NOT NULL");

        if (!r.IsDBNull(13))
            sb.Append($" CONSTRAINT {Q(r.GetString(13))} DEFAULT {r.GetString(14)}");

        return sb.ToString();
    }

    static string FormatType(string type, short maxLength, byte precision, byte scale) =>
        type.ToLowerInvariant() switch
        {
            "char" or "varchar" or "binary" or "varbinary"
                => $"{type}({(maxLength == -1 ? "MAX" : maxLength.ToString())})",
            "nchar" or "nvarchar"
                => $"{type}({(maxLength == -1 ? "MAX" : (maxLength / 2).ToString())})",
            "decimal" or "numeric"
                => $"{type}({precision},{scale})",
            "datetime2" or "datetimeoffset" or "time"
                => $"{type}({scale})",
            _ => type,
        };

    static string Q(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    const string TableSql = @"
SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));

SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
       CONVERT(bigint, ISNULL(ic.seed_value, 1)), CONVERT(bigint, ISNULL(ic.increment_value, 1)),
       c.is_computed, cc.definition, CONVERT(bit, ISNULL(cc.is_persisted, 0)),
       c.collation_name, dc.name, dc.definition
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
LEFT JOIN sys.identity_columns  ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
LEFT JOIN sys.computed_columns  cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE c.object_id = @id
ORDER BY c.column_id;

SELECT kc.name, RTRIM(kc.[type]), i.[type],
       STUFF((SELECT ', ' + QUOTENAME(col.name) + CASE WHEN ic2.is_descending_key = 1 THEN ' DESC' ELSE '' END
              FROM sys.index_columns ic2
              JOIN sys.columns col ON col.object_id = ic2.object_id AND col.column_id = ic2.column_id
              WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.is_included_column = 0
              ORDER BY ic2.key_ordinal
              FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '')
FROM sys.key_constraints kc
JOIN sys.indexes i ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
WHERE kc.parent_object_id = @id
ORDER BY CASE kc.[type] WHEN 'PK' THEN 0 ELSE 1 END, kc.name;

SELECT fk.name,
       QUOTENAME(rs.name) + '.' + QUOTENAME(rt.name),
       STUFF((SELECT ', ' + QUOTENAME(pc.name)
              FROM sys.foreign_key_columns fkc
              JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
              WHERE fkc.constraint_object_id = fk.object_id
              ORDER BY fkc.constraint_column_id
              FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, ''),
       STUFF((SELECT ', ' + QUOTENAME(rc.name)
              FROM sys.foreign_key_columns fkc
              JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
              WHERE fkc.constraint_object_id = fk.object_id
              ORDER BY fkc.constraint_column_id
              FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, ''),
       fk.delete_referential_action_desc, fk.update_referential_action_desc
FROM sys.foreign_keys fk
JOIN sys.tables  rt ON rt.object_id = fk.referenced_object_id
JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
WHERE fk.parent_object_id = @id
ORDER BY fk.name;

SELECT i.name, i.type_desc, i.is_unique,
       STUFF((SELECT ', ' + QUOTENAME(col.name) + CASE WHEN ic2.is_descending_key = 1 THEN ' DESC' ELSE '' END
              FROM sys.index_columns ic2
              JOIN sys.columns col ON col.object_id = ic2.object_id AND col.column_id = ic2.column_id
              WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.is_included_column = 0
              ORDER BY ic2.key_ordinal
              FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, ''),
       STUFF((SELECT ', ' + QUOTENAME(col.name)
              FROM sys.index_columns ic2
              JOIN sys.columns col ON col.object_id = ic2.object_id AND col.column_id = ic2.column_id
              WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.is_included_column = 1
              ORDER BY ic2.index_column_id
              FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, ''),
       i.filter_definition
FROM sys.indexes i
WHERE i.object_id = @id AND i.[type] IN (1, 2)
  AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
ORDER BY i.name;";
}
