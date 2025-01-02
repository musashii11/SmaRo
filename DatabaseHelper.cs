using System;
using System.Data.SQLite;
using System.IO;

namespace smaro_scp_app
{
    internal class DatabaseHelper
    {
        private static string appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "smaro_scp_app","db");
        private string dbPath = Path.Combine(appDataFolder, "database.sqlite");
        private readonly string _connectionString;

        public DatabaseHelper()
        {
            // Create the appData folder if it does not exist
            if (!Directory.Exists(appDataFolder))
            {
                Directory.CreateDirectory(appDataFolder);
            }

            // Initialize the connection string
            _connectionString = $"Data Source={dbPath};Version=3;";
        }

        public SQLiteConnection GetConnection()
        {
            return new SQLiteConnection(_connectionString);
        }
        
        public void DeleteOldRecords()
        {
            try
            {
                using (var connection = GetConnection())
                {
                    connection.Open();

                    // Adjust the query to delete records older than 1 day
                    string deleteQuery = @"
                    DELETE FROM dicom_metadata
                    WHERE received_at < datetime('now', '-1 day')";

                    using (var command = new SQLiteCommand(deleteQuery, connection))
                    {
                        int rowsAffected = command.ExecuteNonQuery();
                        Console.WriteLine($"{rowsAffected} rows deleted that were older than 1 day.");
                    }
                }
            }
            catch (SQLiteException ex)
            {
                Console.WriteLine($"SQLite error during deletion: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"General error during deletion: {ex.Message}");
            }
        }

        public void InitializeDatabase()
        {
            using (var connection = GetConnection())
            {
                connection.Open();

                // Create the Auth table
                string createAuthTableQuery = @"
                    CREATE TABLE IF NOT EXISTS auth (
                        id INTEGER PRIMARY KEY,
                        name TEXT,
                        mobile TEXT,
                        email TEXT,
                        client_name TEXT,
                        branch_name TEXT,
                        client_id INTEGER,
                        branch_id INTEGER,
                        port INTEGER
                    )";
                using (var command = new SQLiteCommand(createAuthTableQuery, connection))
                {
                    command.ExecuteNonQuery();
                }

                // Create the DICOM Metadata table
                string dicomMetadataTableQuery = @"
                    CREATE TABLE IF NOT EXISTS dicom_metadata (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        patient_name TEXT,
                        patient_id TEXT ,
                        gender TEXT,
                        age INTEGER,
                        study_instance_uid TEXT UNIQUE,
                        series_instance_uid TEXT,
                        sop_instance_uid TEXT,
                        study_date TEXT,
                        study_time TEXT,
                        modality TEXT,
                        manufacturer TEXT,
                        institution_name TEXT,
                        status TEXT DEFAULT 'In Progress',
                        file_path TEXT,
                        image_count INTEGER DEFAULT 1,
                        received_at DATETIME DEFAULT CURRENT_TIMESTAMP
                    )";

                using (var command = new SQLiteCommand(dicomMetadataTableQuery, connection))
                {
                    command.ExecuteNonQuery();
                }
            }
        }
    }
}
