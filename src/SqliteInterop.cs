using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PrintAway.Storage
{
    public sealed class SqliteDatabase : IDisposable
    {
        private const string DllName = "winsqlite3.dll";
        private const int SQLITE_OK = 0;
        private const int SQLITE_BUSY = 5;
        private const int SQLITE_LOCKED = 6;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_DONE = 101;

        private const int SQLITE_INTEGER = 1;
        private const int SQLITE_FLOAT = 2;
        private const int SQLITE_TEXT = 3;
        private const int SQLITE_BLOB = 4;
        private const int SQLITE_NULL = 5;

        private static readonly IntPtr SQLITE_TRANSIENT = new IntPtr(-1);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open(byte[] filename, out IntPtr db);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close_v2(IntPtr db);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, out IntPtr pzTail);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_count(IntPtr stmt);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_name(IntPtr stmt, int N);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_type(IntPtr stmt, int iCol);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern double sqlite3_column_double(IntPtr stmt, int iCol);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_null(IntPtr stmt, int index);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int n, IntPtr destructor);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_parameter_index(IntPtr stmt, byte[] zName);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_last_insert_rowid(IntPtr db);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_changes(IntPtr db);

        private IntPtr _db;
        private readonly object _syncRoot = new object();

        public SqliteDatabase(string filePath)
        {
            byte[] pathBytes = ToUtf8NullTerminated(filePath);
            int rc = sqlite3_open(pathBytes, out _db);
            if (rc != SQLITE_OK)
            {
                string err = GetErrorMessage();
                if (_db != IntPtr.Zero) sqlite3_close_v2(_db);
                throw new InvalidOperationException("Failed to open SQLite database: " + err);
            }
            sqlite3_busy_timeout(_db, 5000);
            ExecuteNonQuery("PRAGMA journal_mode=WAL;");
            ExecuteNonQuery("PRAGMA synchronous=NORMAL;");
            ExecuteNonQuery("PRAGMA foreign_keys=ON;");
        }

        public object SyncRoot { get { return _syncRoot; } }

        public void RunInTransaction(Action action)
        {
            lock (_syncRoot)
            {
                ExecuteNonQuery("BEGIN IMMEDIATE TRANSACTION;");
                try
                {
                    action();
                    ExecuteNonQuery("COMMIT;");
                }
                catch
                {
                    try { ExecuteNonQuery("ROLLBACK;"); } catch { }
                    throw;
                }
            }
        }

        public T RunInTransaction<T>(Func<T> func)
        {
            lock (_syncRoot)
            {
                ExecuteNonQuery("BEGIN IMMEDIATE TRANSACTION;");
                try
                {
                    T result = func();
                    ExecuteNonQuery("COMMIT;");
                    return result;
                }
                catch
                {
                    try { ExecuteNonQuery("ROLLBACK;"); } catch { }
                    throw;
                }
            }
        }

        public int ExecuteNonQuery(string sql, IDictionary<string, object> parameters = null)
        {
            lock (_syncRoot)
            {
                string[] statements = SplitStatements(sql);
                int totalChanges = 0;
                foreach (string stmtSql in statements)
                {
                    if (string.IsNullOrWhiteSpace(stmtSql)) continue;
                    IntPtr stmt = Prepare(stmtSql, parameters);
                    try
                    {
                        int rc = StepWithRetry(stmt);
                        if (rc != SQLITE_DONE && rc != SQLITE_ROW)
                        {
                            throw new InvalidOperationException("SQLite step error (" + rc + "): " + GetErrorMessage() + " | SQL: " + stmtSql);
                        }
                        totalChanges += sqlite3_changes(_db);
                    }
                    finally
                    {
                        sqlite3_finalize(stmt);
                    }
                }
                return totalChanges;
            }
        }

        public long ExecuteInsert(string sql, IDictionary<string, object> parameters = null)
        {
            lock (_syncRoot)
            {
                ExecuteNonQuery(sql, parameters);
                return sqlite3_last_insert_rowid(_db);
            }
        }

        public object ExecuteScalar(string sql, IDictionary<string, object> parameters = null)
        {
            lock (_syncRoot)
            {
                var rows = Query(sql, parameters);
                if (rows.Count == 0) return null;
                foreach (var kv in rows[0])
                {
                    return kv.Value;
                }
                return null;
            }
        }

        public string ExecuteScalarString(string sql, IDictionary<string, object> parameters = null, string defaultVal = "")
        {
            object val = ExecuteScalar(sql, parameters);
            if (val == null || val is DBNull) return defaultVal;
            return Convert.ToString(val, CultureInfo.InvariantCulture);
        }

        public long ExecuteScalarLong(string sql, IDictionary<string, object> parameters = null, long defaultVal = 0)
        {
            object val = ExecuteScalar(sql, parameters);
            if (val == null || val is DBNull) return defaultVal;
            return Convert.ToInt64(val, CultureInfo.InvariantCulture);
        }

        public double ExecuteScalarDouble(string sql, IDictionary<string, object> parameters = null, double defaultVal = 0.0)
        {
            object val = ExecuteScalar(sql, parameters);
            if (val == null || val is DBNull) return defaultVal;
            return Convert.ToDouble(val, CultureInfo.InvariantCulture);
        }

        public List<Dictionary<string, object>> Query(string sql, IDictionary<string, object> parameters = null)
        {
            lock (_syncRoot)
            {
                var results = new List<Dictionary<string, object>>();
                IntPtr stmt = Prepare(sql, parameters);
                try
                {
                    int colCount = sqlite3_column_count(stmt);
                    var colNames = new string[colCount];
                    for (int i = 0; i < colCount; i++)
                    {
                        colNames[i] = PtrToUtf8String(sqlite3_column_name(stmt, i));
                    }

                    while (true)
                    {
                        int rc = StepWithRetry(stmt);
                        if (rc == SQLITE_ROW)
                        {
                            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                            for (int i = 0; i < colCount; i++)
                            {
                                int colType = sqlite3_column_type(stmt, i);
                                switch (colType)
                                {
                                    case SQLITE_INTEGER:
                                        row[colNames[i]] = sqlite3_column_int64(stmt, i);
                                        break;
                                    case SQLITE_FLOAT:
                                        row[colNames[i]] = sqlite3_column_double(stmt, i);
                                        break;
                                    case SQLITE_TEXT:
                                        int byteLen = sqlite3_column_bytes(stmt, i);
                                        IntPtr txtPtr = sqlite3_column_text(stmt, i);
                                        row[colNames[i]] = PtrToUtf8String(txtPtr, byteLen);
                                        break;
                                    case SQLITE_NULL:
                                    default:
                                        row[colNames[i]] = null;
                                        break;
                                }
                            }
                            results.Add(row);
                        }
                        else if (rc == SQLITE_DONE)
                        {
                            break;
                        }
                        else
                        {
                            throw new InvalidOperationException("SQLite query step error (" + rc + "): " + GetErrorMessage());
                        }
                    }
                }
                finally
                {
                    sqlite3_finalize(stmt);
                }
                return results;
            }
        }

        public Dictionary<string, object> QuerySingle(string sql, IDictionary<string, object> parameters = null)
        {
            var rows = Query(sql, parameters);
            return rows.Count > 0 ? rows[0] : null;
        }

        private int StepWithRetry(IntPtr stmt)
        {
            int attempts = 0;
            while (true)
            {
                int rc = sqlite3_step(stmt);
                if (rc != SQLITE_BUSY && rc != SQLITE_LOCKED)
                    return rc;
                attempts++;
                if (attempts > 25)
                    return rc;
                System.Threading.Thread.Sleep(25);
            }
        }

        private IntPtr Prepare(string sql, IDictionary<string, object> parameters)
        {
            byte[] sqlBytes = ToUtf8NullTerminated(sql);
            IntPtr stmt;
            IntPtr tail;
            int rc = sqlite3_prepare_v2(_db, sqlBytes, sqlBytes.Length - 1, out stmt, out tail);
            if (rc != SQLITE_OK)
            {
                throw new InvalidOperationException("SQLite prepare error (" + rc + "): " + GetErrorMessage() + " | SQL: " + sql);
            }

            if (parameters != null)
            {
                foreach (var kv in parameters)
                {
                    string paramName = kv.Key.StartsWith("@") ? kv.Key : "@" + kv.Key;
                    int idx = sqlite3_bind_parameter_index(stmt, ToUtf8NullTerminated(paramName));
                    if (idx <= 0) continue;

                    object val = kv.Value;
                    if (val == null || val is DBNull)
                    {
                        sqlite3_bind_null(stmt, idx);
                    }
                    else if (val is bool)
                    {
                        sqlite3_bind_int64(stmt, idx, ((bool)val) ? 1L : 0L);
                    }
                    else if (val is int || val is long || val is short || val is byte)
                    {
                        sqlite3_bind_int64(stmt, idx, Convert.ToInt64(val, CultureInfo.InvariantCulture));
                    }
                    else if (val is float || val is double || val is decimal)
                    {
                        sqlite3_bind_double(stmt, idx, Convert.ToDouble(val, CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        string strVal = Convert.ToString(val, CultureInfo.InvariantCulture);
                        byte[] utf8 = Encoding.UTF8.GetBytes(strVal);
                        sqlite3_bind_text(stmt, idx, utf8, utf8.Length, SQLITE_TRANSIENT);
                    }
                }
            }
            return stmt;
        }

        private string[] SplitStatements(string sql)
        {
            if (sql.IndexOf(';') < 0) return new[] { sql };
            var list = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < sql.Length; i++)
            {
                char c = sql[i];
                if (c == '\'')
                {
                    inQuotes = !inQuotes;
                    sb.Append(c);
                }
                else if (c == ';' && !inQuotes)
                {
                    string stmt = sb.ToString().Trim();
                    if (stmt.Length > 0) list.Add(stmt);
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
            string rem = sb.ToString().Trim();
            if (rem.Length > 0) list.Add(rem);
            return list.ToArray();
        }

        private string GetErrorMessage()
        {
            if (_db == IntPtr.Zero) return "Database pointer is null";
            return PtrToUtf8String(sqlite3_errmsg(_db));
        }

        private static byte[] ToUtf8NullTerminated(string s)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(s ?? "");
            byte[] buf = new byte[utf8.Length + 1];
            Buffer.BlockCopy(utf8, 0, buf, 0, utf8.Length);
            return buf;
        }

        private static string PtrToUtf8String(IntPtr ptr, int length = -1)
        {
            if (ptr == IntPtr.Zero) return string.Empty;
            if (length < 0)
            {
                length = 0;
                while (Marshal.ReadByte(ptr, length) != 0)
                {
                    length++;
                }
            }
            if (length == 0) return string.Empty;
            byte[] buf = new byte[length];
            Marshal.Copy(ptr, buf, 0, length);
            return Encoding.UTF8.GetString(buf);
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_db != IntPtr.Zero)
                {
                    sqlite3_close_v2(_db);
                    _db = IntPtr.Zero;
                }
            }
        }
    }
}
