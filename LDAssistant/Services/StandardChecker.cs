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

            // CJK标点替换
            result = result.Replace('。', '.').Replace('、', ',')
                          .Replace('～', '~').Replace('—', '-').Replace('–', '-')
                          .Replace('…', '.').Replace('×', 'x').Replace('÷', '/')
                          .Replace('·', '-').Replace('【', '[').Replace('】', ']')
                          .Replace('（', '(').Replace('）', ')')
                          .Replace('"', '"').Replace('"', '"')
                          .Replace(''', '\'').Replace(''', '\'');

            // 去所有空格
            result = Regex.Replace(result, @"\s+", "");

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

            // 1. 精确 norm_code 匹配
            var record = QuerySingle("SELECT * FROM standards WHERE norm_code = @c LIMIT 1", ("@c", normCode));

            // 2. 精确 code 匹配
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
                result.DetailUrl = record.DetailUrl ?? "";
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

            // 补充 LIKE 搜索
            if (results.Count < limit)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"SELECT * FROM standards WHERE code LIKE @q OR name LIKE @q2 
                                     LIMIT @limit";
                cmd.Parameters.AddWithValue("@q", $"%{query}%");
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
    }
}
