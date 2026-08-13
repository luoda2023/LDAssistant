using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using LDAssistant.Models;

namespace LDAssistant.Services
{
    /// <summary>标准编号检查服务 - 读取 standards.db (SQLite + FTS5)</summary>
    public class StandardChecker : IDisposable
    {
        private SqliteConnection _conn;
        private readonly string _dbPath;

        public StandardChecker(string dbPath)
        {
            _dbPath = dbPath;
            _conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            _conn.Open();
            VerifyDb();
        }

        private void VerifyDb()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM standards";
            var count = cmd.ExecuteScalar();
            System.Diagnostics.Debug.WriteLine($"[StandardChecker] 数据库就绪: {count} 条记录");
        }

        /// <summary>文本标准化（全角→半角、去空格、OCR纠错、大写）</summary>
        public static string NormalizeForMatching(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);

            foreach (char c in text)
            {
                // 全角→半角
                if (c >= '\uFF01' && c <= '\uFF5E')
                    sb.Append((char)(c - 0xFEE0));
                else if (c == '\u3000')
                    sb.Append(' ');
                else
                    sb.Append(c);
            }

            string result = sb.ToString();

            // CJK标点替换（用字符串避免 char 字面量问题）
            result = result
                .Replace("\u3002", ".")   // 。
                .Replace("\u3001", ",")   // 、
                .Replace("\uFF5E", "~")   // ～
                .Replace("\u2014", "-")   // —
                .Replace("\u2013", "-")   // –
                .Replace("\u2026", "...") // …
                .Replace("\u00D7", "x")   // ×
                .Replace("\u00F7", "/")   // ÷
                .Replace("\u00B7", "-")   // ·
                .Replace("\u3010", "[")   // 【
                .Replace("\u3011", "]")   // 】
                .Replace("\uFF08", "(")   // （
                .Replace("\uFF09", ")")   // ）
                .Replace("\u201C", "\"")  // "
                .Replace("\u201D", "\"")  // "
                .Replace("\u2018", "'")   // '
                .Replace("\u2019", "'");  // '

 // 去所有空格
 result = Regex.Replace(result, @"\s+", "");

 // 统一去除规范编号中的分隔符（/ - .），让 GB50001 与 GB/T50001-2017 归一化一致
 result = Regex.Replace(result, @"[/\-.]", "");

 // 去除 GB/T、GB/Z 等推荐/指导标准中的分类字母，使 GB50001 能匹配 GB/T50001
 // （用户常省略 /T，归一化时统一去掉斜杠后的单字母分类符）
 result = Regex.Replace(result, @"^GB[TZJC]", "GB", RegexOptions.IgnoreCase);
 result = Regex.Replace(result, @"^DB/T", "DB", RegexOptions.IgnoreCase);

 // OCR纠错
            result = Regex.Replace(result, @"CJJJ", "CJJ", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"DGJ(?=\d)", "DG/TJ", RegexOptions.IgnoreCase);
            // l/I 误认为 1
            result = Regex.Replace(result, @"[LlI](?=[A-Z\d])", "1");
            result = Regex.Replace(result, @"(?<=[A-Z\d])[LlI]", "1");
            // O 误认为 0
            result = Regex.Replace(result, @"[Oo](?=\d)", "0");
            result = Regex.Replace(result, @"(?<=\d)[Oo]", "0");

            return result.ToUpper();
        }

        /// <summary>检查单个规范编号</summary>
        public CheckResult CheckCode(string code, string name = "")
        {
            var result = new CheckResult
            {
                Code = code,
                Name = name,
                Status = "未找到"
            };

            var normCode = NormalizeForMatching(code);
            var normName = NormalizeForMatching(name);

 // 归一化SQL表达式：去分隔符 + GB/T→GB 变体（与 NormalizeForMatching 保持一致）
 const string normExpr = "REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE({0},'/',''),'-',''),' ',''),'.',''),'GBT','GB'),'GBZ','GB'),'GBC','GB'),'GBJ','GB')";

 // 1. 精确 norm_code 匹配（去分隔符+分类字母后比较，GB50001 ↔ GB/T50001-2017）
 var record = QuerySingle(
 $"SELECT * FROM standards WHERE {string.Format(normExpr, "norm_code")} = @c LIMIT 1",
 ("@c", normCode));

 // 2. 精确 code 匹配（同样去分隔符+分类字母）
 if (record == null)
 record = QuerySingle(
 $"SELECT * FROM standards WHERE {string.Format(normExpr, "code")} = @c LIMIT 1",
 ("@c", normCode));

 // 2b. 原始 code 匹配（兜底，处理库里本身就是无斜杠的情况）
 if (record == null)
 record = QuerySingle("SELECT * FROM standards WHERE code = @c LIMIT 1", ("@c", code));

            // 3. 名称匹配
            if (record == null && !string.IsNullOrEmpty(normName))
                record = QuerySingle("SELECT * FROM standards WHERE norm_name = @n LIMIT 1", ("@n", normName));

            // 4. FTS5 模糊匹配
            if (record == null)
            {
                var similar = FindSimilarCodes(normCode, 1);
                if (similar.Count > 0)
                    record = similar[0];
            }

            if (record != null)
            {
                result.Name = string.IsNullOrEmpty(name) ? record.Name : name;
                result.Status = string.IsNullOrEmpty(record.Status) ? "现行" : record.Status;
                result.Replacement = record.ReplacementRaw ?? "";
                result.Publisher = record.Publisher ?? "";
                result.Source = record.SourceType ?? "";
            }

            return result;
        }

        /// <summary>模糊搜索相似编号</summary>
        public List<StandardRecord> FindSimilarCodes(string query, int limit = 20)
        {
            var results = new List<StandardRecord>();
            var norm = NormalizeForMatching(query);
            if (string.IsNullOrEmpty(norm)) return results;

            // 先用 FTS5
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"SELECT * FROM standards WHERE standards_fts MATCH @q 
                                     ORDER BY rank LIMIT @limit";
                cmd.Parameters.AddWithValue("@q", norm);
                cmd.Parameters.AddWithValue("@limit", limit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    results.Add(MapRecord(reader));
            }
            catch { }

 // 补充 LIKE 搜索（编号字段做分隔符归一化匹配，含 GB/T→GB 变体）
 if (results.Count < limit)
 {
 using var cmd = _conn.CreateCommand();
 // 归一化SQL：去斜杠/连字符/空格/点，再把 GBT→GB（与 NormalizeForMatching 保持一致）
 const string normExpr = "REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE({0},'/',''),'-',''),' ',''),'.',''),'GBT','GB'),'GBZ','GB'),'GBC','GB'),'GBJ','GB')";
 cmd.CommandText = $@"SELECT * FROM standards WHERE
 {string.Format(normExpr, "norm_code")} LIKE @q
 OR {string.Format(normExpr, "code")} LIKE @q
 OR name LIKE @q2
 LIMIT @limit";
 cmd.Parameters.AddWithValue("@q", $"%{norm}%");
 cmd.Parameters.AddWithValue("@q2", $"%{query}%");
 cmd.Parameters.AddWithValue("@limit", limit - results.Count);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    results.Add(MapRecord(reader));
            }

            return results;
        }

        private StandardRecord QuerySingle(string sql, params (string, object)[] args)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args)
                cmd.Parameters.AddWithValue(name, value);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
                return MapRecord(reader);
            return null;
        }

        private StandardRecord MapRecord(SqliteDataReader r)
        {
            return new StandardRecord
            {
                Id = r.GetInt32(r.GetOrdinal("id")),
                Code = r.GetString(r.GetOrdinal("code")),
                Name = r.GetString(r.GetOrdinal("name")),
                Status = r.GetString(r.GetOrdinal("status")),
                Publisher = r.GetString(r.GetOrdinal("publisher")),
                ImplementDate = r.GetString(r.GetOrdinal("implement_date")),
                DetailUrl = r.GetString(r.GetOrdinal("detail_url")),
                ReplacementRaw = r.GetString(r.GetOrdinal("replacement_raw")),
                ReplacementParsed = r.GetString(r.GetOrdinal("replacement_parsed")),
                SourceType = r.GetString(r.GetOrdinal("source_type"))
            };
        }

    public void Dispose()
    {
        try { _conn?.Close(); _conn?.Dispose(); } catch { }
    }

    // ═══════════════════════════════════════════════════════════
    //  手动查询功能
    // ═══════════════════════════════════════════════════════════

    /// <summary>获取所有分类（source_type）</summary>
    public List<(string type, int count)> GetCategories()
    {
        var list = new List<(string, int)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT source_type, COUNT(*) FROM standards GROUP BY source_type ORDER BY COUNT(*) DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var t = reader.IsDBNull(0) ? "未知" : reader.GetString(0);
            var c = reader.GetInt32(1);
            list.Add((t, c));
        }
        return list;
    }

    /// <summary>获取总记录数</summary>
    public long GetTotalCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM standards";
        return (long)cmd.ExecuteScalar();
    }

    /// <summary>按状态统计</summary>
    public List<(string status, int count)> GetStatusStats()
    {
        var list = new List<(string, int)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT status, COUNT(*) FROM standards GROUP BY status ORDER BY COUNT(*) DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var s = reader.IsDBNull(0) ? "未知" : reader.GetString(0);
            var c = reader.GetInt32(1);
            list.Add((s, c));
        }
        return list;
    }

    /// <summary>搜索规范：可在全库或指定分类内查询</summary>
    public List<StandardRecord> Search(string keyword, string category = "", int limit = 200)
    {
        var results = new List<StandardRecord>();
        if (string.IsNullOrWhiteSpace(keyword)) return results;

        // 先用 FTS5
        try
        {
            using var cmd = _conn.CreateCommand();
            var sql = "SELECT * FROM standards WHERE standards_fts MATCH @q";
            if (!string.IsNullOrEmpty(category))
                sql += " AND source_type = @cat";
            sql += " ORDER BY rank LIMIT @limit";
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@q", keyword.Trim());
            if (!string.IsNullOrEmpty(category))
                cmd.Parameters.AddWithValue("@cat", category);
            cmd.Parameters.AddWithValue("@limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(MapRecord(reader));
        }
        catch { }

 // 补充 LIKE 搜索（编号字段做分隔符归一化匹配，含 GB/T→GB 变体）
 if (results.Count < limit)
 {
 var norm = NormalizeForMatching(keyword);
 using var cmd = _conn.CreateCommand();
 const string normExpr = "REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE({0},'/',''),'-',''),' ',''),'.',''),'GBT','GB'),'GBZ','GB'),'GBC','GB'),'GBJ','GB')";
 var sql = $@"SELECT * FROM standards WHERE
 {string.Format(normExpr, "norm_code")} LIKE @q
 OR {string.Format(normExpr, "code")} LIKE @q
 OR name LIKE @q2";
 if (!string.IsNullOrEmpty(category))
 sql += " AND source_type = @cat";
 sql += " LIMIT @limit";
 cmd.CommandText = sql;
 cmd.Parameters.AddWithValue("@q", $"%{norm}%");
 cmd.Parameters.AddWithValue("@q2", $"%{keyword}%");
 if (!string.IsNullOrEmpty(category))
 cmd.Parameters.AddWithValue("@cat", category);
 cmd.Parameters.AddWithValue("@limit", limit - results.Count);
 using var reader = cmd.ExecuteReader();
 while (reader.Read())
 results.Add(MapRecord(reader));
 }

 return results;
 }

 ///
 public List<StandardRecord> SearchByStatus(string keyword, string status, string category = "", int limit = 200)
    {
        var results = new List<StandardRecord>();
        if (string.IsNullOrWhiteSpace(keyword)) return results;

 var norm = NormalizeForMatching(keyword);
 using var cmd = _conn.CreateCommand();
 const string normExpr = "REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE({0},'/',''),'-',''),' ',''),'.',''),'GBT','GB'),'GBZ','GB'),'GBC','GB'),'GBJ','GB')";
 var sql = $@"SELECT * FROM standards WHERE (
 {string.Format(normExpr, "norm_code")} LIKE @q
 OR {string.Format(normExpr, "code")} LIKE @q
 OR name LIKE @q2) AND status = @status";
 if (!string.IsNullOrEmpty(category))
 sql += " AND source_type = @cat";
 sql += " LIMIT @limit";
 cmd.CommandText = sql;
 cmd.Parameters.AddWithValue("@q", $"%{norm}%");
 cmd.Parameters.AddWithValue("@q2", $"%{keyword}%");
        cmd.Parameters.AddWithValue("@status", status);
        if (!string.IsNullOrEmpty(category))
            cmd.Parameters.AddWithValue("@cat", category);
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(MapRecord(reader));

        return results;
    }
}
}
