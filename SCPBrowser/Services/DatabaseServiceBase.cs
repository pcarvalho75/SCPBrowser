using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SCPBrowser.Services
{
    /// <summary>
    /// Base class for all SQLite database services.
    /// Provides helper methods that eliminate repetitive connection/command boilerplate.
    /// </summary>
    public abstract class DatabaseServiceBase
    {
        protected readonly string _projectDbPath;

        protected DatabaseServiceBase(string projectDbPath)
        {
            _projectDbPath = projectDbPath;
        }

        protected string ConnectionString => $"Data Source={_projectDbPath}";

        /// <summary>
        /// Executes a non-query command (INSERT, UPDATE, DELETE, CREATE TABLE, etc.)
        /// Returns the number of rows affected.
        /// </summary>
        protected async Task<int> ExecuteNonQueryAsync(string sql, Action<SqliteCommand> paramSetup = null)
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    paramSetup?.Invoke(command);
                    return await command.ExecuteNonQueryAsync();
                }
            }
        }

        /// <summary>
        /// Executes a scalar query and returns the result cast to T.
        /// Returns defaultValue if the result is null or DBNull.
        /// </summary>
        protected async Task<T> ExecuteScalarAsync<T>(string sql, Action<SqliteCommand> paramSetup = null, T defaultValue = default)
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    paramSetup?.Invoke(command);
                    var result = await command.ExecuteScalarAsync();

                    if (result == null || result == DBNull.Value)
                        return defaultValue;

                    return (T)Convert.ChangeType(result, typeof(T));
                }
            }
        }

        /// <summary>
        /// Executes a query and maps each row to T using the provided mapper function.
        /// </summary>
        protected async Task<List<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> mapper, Action<SqliteCommand> paramSetup = null)
        {
            var results = new List<T>();

            using (var connection = new SqliteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    paramSetup?.Invoke(command);

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            results.Add(mapper(reader));
                        }
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Provides an open connection for complex operations (transactions, multiple commands, etc.)
        /// </summary>
        protected async Task WithConnectionAsync(Func<SqliteConnection, Task> action)
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                await action(connection);
            }
        }

        /// <summary>
        /// Provides an open connection and returns a result. For complex operations that need a return value.
        /// </summary>
        protected async Task<T> WithConnectionAsync<T>(Func<SqliteConnection, Task<T>> action)
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                return await action(connection);
            }
        }
    }
}
