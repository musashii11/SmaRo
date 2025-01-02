using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FellowOakDicom.Network;

namespace smaro_scp_app
{
    public partial class ViewReportsWindow : Window
    {
        private readonly DatabaseHelper _databaseHelper;
        private IDicomServer _dicomServer;
        public static Action UpdateDataGrid;
        private int _currentPage = 1;
        private int _rowsPerPage = 20;
        private ObservableCollection<Report> _allReports = new ObservableCollection<Report>(); // Assume you load this from a database
        private ObservableCollection<Report> _pagedReports = new ObservableCollection<Report>();

        public ViewReportsWindow()
        {
            InitializeComponent();
            _databaseHelper = new DatabaseHelper();
            LoadData();
            UpdateDataGrid = LoadData;
            _databaseHelper.DeleteOldRecords();
        }

        private void LoadData()
        {
            try
            {
                using (var connection = _databaseHelper.GetConnection())
                {
                    connection.Open();

                    string query = "SELECT * FROM dicom_metadata ORDER BY received_at DESC";

                    using (var command = new SQLiteCommand(query, connection))
                    using (var reader = command.ExecuteReader())
                    {
                        var reports = new List<Report>();
                        int index = 1;
                        while (reader.Read())
                        {
                            reports.Add(new Report
                            {
                                SerialNo = index++,
                                PatientName = reader.GetString(reader.GetOrdinal("patient_name")),
                                PatientID = reader.GetString(reader.GetOrdinal("patient_id")),
                                Gender = reader.GetString(reader.GetOrdinal("gender")),
                                Age = reader.GetString(reader.GetOrdinal("age")),
                                StudyInstanceUID = reader.GetString(reader.GetOrdinal("study_instance_uid")),
                                Modality = reader.GetString(reader.GetOrdinal("modality")),
                                Manufacturer = reader.GetString(reader.GetOrdinal("manufacturer")),
                                InstitutionName = reader.GetString(reader.GetOrdinal("institution_name")),
                                Status = reader.GetString(reader.GetOrdinal("status")),
                                FilePath = reader.GetString(reader.GetOrdinal("file_path")),
                                ReceivedAt = reader.GetDateTime(reader.GetOrdinal("received_at")),
                                ImageCount = reader.GetInt32(reader.GetOrdinal("image_count")) // Added ImageCount
                            });
                        }
                        ReportsDataGrid.ItemsSource = reports;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load data: {ex.Message}");
            }
        }

        
        private void LoadPagedReports()
        {
            var skip = (_currentPage - 1) * _rowsPerPage;
            var pagedData = _allReports.Skip(skip).Take(_rowsPerPage).ToList();
            _pagedReports.Clear();
            foreach (var report in pagedData)
                _pagedReports.Add(report);

            PageInfo.Text = $"Page {_currentPage} of {Math.Ceiling((double)_allReports.Count / _rowsPerPage)}";
        }

        private void PreviousPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                LoadPagedReports();
            }
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < Math.Ceiling((double)_allReports.Count / _rowsPerPage))
            {
                _currentPage++;
                LoadPagedReports();
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ReportsDataGrid.ItemsSource = _pagedReports;
            LoadPagedReports();
        }
        
        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            Logout();
        }

        private async void Logout()
        {
            try
            {
                using (var connection = _databaseHelper.GetConnection())
                {
                    await connection.OpenAsync();

                    string query = @"DELETE FROM auth WHERE id = 1";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        int rowsAffected = await command.ExecuteNonQueryAsync();
                        if (rowsAffected > 0)
                        {
                            MainWindow mainWindow = new MainWindow();
                            mainWindow.Show();
                            this.Close();
                        }
                        else
                        {
                            MessageBox.Show("Logout failed. No rows affected.");
                        }
                    }
                }
            }
            catch (SQLiteException ex)
            {
                MessageBox.Show($"Database error: {ex.Message}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}");
            }
        }

        private  void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button?.CommandParameter is string filePath)
            {
                 RetryFile(filePath);
            }
        }
        
        private void Port_Config(object sender, RoutedEventArgs e)
        {
            ConfigWindow configWindow = new ConfigWindow();
            configWindow.Show();
            this.Close();
        }
        private void ServerToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_dicomServer == null)
            {
                StartDicomServer();
                ServerToggleButton.Content = "Stop Server";
                ServerToggleButton.Background = new SolidColorBrush(Colors.IndianRed); // Change to a "stop" color
            }
            else
            {
                StopDicomServer();
                ServerToggleButton.Content = "Start Server";
                ServerToggleButton.Background = new SolidColorBrush(Colors.SteelBlue); // Change back to a "start" color
            }
        }
        
        
        private async void StartDicomServer()
        {
            string query = @"SELECT port FROM auth WHERE id = 1";

            try
            {
                using (var connection = _databaseHelper.GetConnection())
                {
                    await connection.OpenAsync();
                    using (var command = new SQLiteCommand(query, connection))
                    {
                        object result = await command.ExecuteScalarAsync();

                        if (result != null)
                        {
                            int port = Convert.ToInt32(result);

                            if (_dicomServer != null)
                            {
                                MessageBox.Show($"DICOM server is already running on port {port}.");
                                return;
                            }

                            _dicomServer = DicomServerFactory.Create<DicomReceiver>(port);
                            MessageBox.Show($"Listening for DICOM files on port {port}...");
                            ServerToggleButton.Content = "Stop Server";
                        }
                        else
                        {
                            MessageBox.Show("Failed to retrieve port information from database.");
                        }
                    }
                }
            }
            catch (SQLiteException ex)
            {
                MessageBox.Show($"Database error: {ex.Message}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}");
            }
        }

        private void StopDicomServer()
        {
            try
            {
                if (_dicomServer != null)
                {
                    _dicomServer.Dispose();
                    _dicomServer = null;
                    MessageBox.Show("DICOM server stopped successfully.");
                    ServerToggleButton.Content = "Start Server";
                }
                else
                {
                    MessageBox.Show("DICOM server is not running.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred while stopping the server: {ex.Message}");
            }
        }


        private async void RetryFile(string filePath)
        {
            UpdateStatusInDatabase(filePath, "In Progress");
            bool isSent = await SendDicomFileToOrthanc(filePath);
            if (isSent)
            {
                UpdateStatusInDatabase(filePath, "Completed"); // Image count is incremented in UpdateStatusInDatabase
                File.Delete(filePath); // Delete only if sent successfully
            }
            else
            {
                UpdateStatusInDatabase(filePath, "Failed");
            }

            LoadData(); // Refresh the DataGrid
        }



        private async Task<bool> SendDicomFileToOrthanc(string filePath)
{
    try
    {
        // Retrieve branchId and clientId from the auth table
        int branchId = 0, clientId = 0;
        using (var connection = _databaseHelper.GetConnection())
        {
            connection.Open();
            string query = "SELECT branch_id, client_id FROM auth WHERE id = 1";

            using (var command = new SQLiteCommand(query, connection))
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    branchId = reader.GetInt32(reader.GetOrdinal("branch_id"));
                    clientId = reader.GetInt32(reader.GetOrdinal("client_id"));
                }
                else
                {
                    MessageBox.Show("Failed to retrieve branchId and clientId from the database.");
                    return false;
                }
            }
        }

        // Read the DICOM file and send it to Orthanc
        using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
        {
            var content = new MultipartFormDataContent();
            var dicomContent = new ByteArrayContent(await ReadFully(fileStream));
            dicomContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/dicom");
            content.Add(dicomContent, "file", Path.GetFileName(filePath));
            content.Add(new StringContent(branchId.ToString()), "branch_id");
            content.Add(new StringContent(clientId.ToString()), "client_id");

            var response = await new HttpClient().PostAsync("https://api.smaro.app/api/console/orthanc/upload", content);

            return response.IsSuccessStatusCode;
        }
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Error sending file to Orthanc: {ex.Message}");
        return false;
    }
}

// Helper method to read a file stream into a byte array
private async Task<byte[]> ReadFully(Stream input)
{
    using (var memoryStream = new MemoryStream())
    {
        await input.CopyToAsync(memoryStream);
        return memoryStream.ToArray();
    }
}


private void UpdateStatusInDatabase(string filePath, string status)
{
    using (var connection = _databaseHelper.GetConnection())
    {
        connection.Open();

        // Increment the image count only when the file is successfully sent
        string query = status == "Completed"
            ? "UPDATE dicom_metadata SET status = @Status, image_count = image_count + 1 WHERE file_path = @FilePath"
            : "UPDATE dicom_metadata SET status = @Status WHERE file_path = @FilePath";

        using (var command = new SQLiteCommand(query, connection))
        {
            command.Parameters.AddWithValue("@Status", status);
            command.Parameters.AddWithValue("@FilePath", filePath);
            command.ExecuteNonQuery();
        }
    }
}

    }

    public class Report
    {
        public int SerialNo { get; set; }
        public string PatientName { get; set; }
        public string PatientID { get; set; }
        public string Gender { get; set; }
        public string Age { get; set; }
        public string StudyInstanceUID { get; set; }
        public string Modality { get; set; }
        public string Manufacturer { get; set; }
        public string InstitutionName { get; set; }
        public string Status { get; set; }
        public string FilePath { get; set; }
        public DateTime ReceivedAt { get; set; }
        public int ImageCount { get; set; } // Added ImageCount
    }

}
