using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace smaro_scp_app
{
    /// <summary>
    /// Interaction logic for ConfigWindow.xaml
    /// </summary>
    public partial class ConfigWindow : Window
    {
        private readonly DatabaseHelper _databaseHelper;
        public ConfigWindow()
        {
            InitializeComponent();
            _databaseHelper = new DatabaseHelper();
            GetPortAsync();
        }

        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            Logout();
        }

        private async void Logout()
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
                }
            }
        }

        private async Task GetPortAsync()
        {
            try
            {
                using (var connection = _databaseHelper.GetConnection())
                {
                    await connection.OpenAsync();
                    string query = "SELECT port FROM auth WHERE id = 1";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        var result = await command.ExecuteScalarAsync();
                        if (result != null && int.TryParse(result.ToString(), out int port))
                        {
                            // Port retrieved successfully, set it to PortTextBox
                            PortTextBox.Text = port.ToString();
                        }
                        else
                        {
                            // Handle the case where the port is not retrieved
                            Console.WriteLine("Port not found or invalid.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Handle exceptions if necessary
                Console.WriteLine($"Error: {ex.Message}");
            }
        }


        private void Start_Monitoring_Button(object sender, RoutedEventArgs e)
        {
            ViewReportsWindow viewReportsWindow = new ViewReportsWindow();
            AddPort();
            viewReportsWindow.Show();
            this.Close();
        }

        private async void AddPort()
        {
            var port = PortTextBox.Text;
            using (var connection = _databaseHelper.GetConnection())
            {
                await connection.OpenAsync();
                string query = @"UPDATE auth SET port = @port WHERE id = 1";
                
                using (var command = new SQLiteCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@port", port);  // Fixed ID for single-row storage

                    command.ExecuteNonQuery();
                }
                
            }
        }
        
        
    }
}
