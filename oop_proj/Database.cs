using Microsoft.Data.SqlClient;
using System;
using System.Data.Common;
using System.Diagnostics;
using System.Threading.Tasks;

namespace oop_proj
{
    internal class Database : IAsyncDisposable
    {
        private SqlConnection? connection;
        private readonly string connectionString;

        public Database(SqlConnectionStringBuilder dbConnectionString)
        {
            connectionString = dbConnectionString.ConnectionString;
            this._connect();
        }

       
        private async void _connect()
        {
            await this.Connect();

            if(!await this.checkIfTablesExists())
            {
                AppEvents.UpdateStatus("Tables do not exist, setting up schema...");
            }
        }

        public async Task Connect()
        {
            if (connection?.State == System.Data.ConnectionState.Open)
            {
                return;
            }

            try
            {
                connection = new SqlConnection(connectionString);

                await connection.OpenAsync();
                Debug.Write("Database connection opened successfully.");
            }
            catch (SqlException e)
            {
                Debug.Write($"SQL Error: {e.Message}");
                connection = null;
            }
            catch (Exception e)
            {
                Debug.Write(e.ToString());
                connection = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (connection != null)
            {
                await connection.DisposeAsync();
                connection = null;
                Console.WriteLine("Connection disposed.");
            }
        }

        private async Task<bool> TableExistsAsync(string tableName)
        {
            if (connection == null)
            {
                throw new InvalidOperationException("Connection is not initialized. Call Connect() first.");
            }
            var sql = @"
        SELECT COUNT(*) 
        FROM INFORMATION_SCHEMA.TABLES 
        WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @tableName";

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@tableName", tableName);

            int? count = (int?)await command.ExecuteScalarAsync();

            return count.GetValueOrDefault() > 0;

        }
        private async Task<bool> checkIfTablesExists()
        {
            int sum = 0;
            if (connection == null)
            {
                throw new InvalidOperationException("Connection is not initialized. Call Connect() first.");
            }
            string[] tables = ["users","attendance","admin"];

            foreach(string table in tables)
            {
                sum += (await this.TableExistsAsync(table)) ? 1 : 0;
            }
            
            // if true it means all tables exists, false meaning not all tables exists
            return sum == tables.Length;
        }

        public async Task<object?> SetupSchema()
        {
            if(connection == null)
            {
                throw new InvalidOperationException("Connection is not initialized. Call Connect() first.");
            }

            string sqlScript = System.IO.File.ReadAllText("C:\\Users\\Adrian\\source\\repos\\oop_proj\\oop_proj\\SCHEMA.sql");

            await using var command = new SqlCommand(sqlScript, connection);
            return await command.ExecuteNonQueryAsync();
        }

        public async Task<object?> GetFirstItemName()
        {
            if (connection == null)
            {
                throw new InvalidOperationException("Connection is not initialized. Call Connect() first.");
            }

            await using var command = new SqlCommand("SELECT TOP 1 Name FROM Items", connection);
            return await command.ExecuteScalarAsync();
        }
    }
}