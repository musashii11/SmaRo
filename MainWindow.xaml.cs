using System;
using System.Data.SQLite;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;

namespace smaro_scp_app
{
    public partial class MainWindow : Window
    {
        private readonly HttpClient _httpClient;
        private readonly DatabaseHelper _databaseHelper;

        public MainWindow()
        {
            InitializeComponent();
            _databaseHelper = new DatabaseHelper();
            _httpClient = new HttpClient();
            _databaseHelper.InitializeDatabase();
            CheckLogin();
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Validate inputs
                if (string.IsNullOrWhiteSpace(UsernameTextBox.Text) || string.IsNullOrWhiteSpace(PasswordBox.Password))
                {
                    MessageBox.Show("Please enter both username and password.");
                    return;
                }

                string mobile = UsernameTextBox.Text;
                string password = PasswordBox.Password;

                bool loginSuccess = await Login(mobile, password);

                if (loginSuccess)
                {
                    ViewReportsWindow viewReportsWindow = new ViewReportsWindow();
                    viewReportsWindow.Show();
                    this.Close();
                }
                else
                {
                    MessageBox.Show("Login failed. Please try again.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}");
            }
        }

        private async void CheckLogin()
        {
            try
            {
                using (var connection = _databaseHelper.GetConnection())
                {
                    await connection.OpenAsync();

                    string searchQuery = @"SELECT * FROM auth WHERE id = 1";

                    using (var command = new SQLiteCommand(searchQuery, connection))
                    {
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                ViewReportsWindow viewReportsWindow = new ViewReportsWindow();
                                viewReportsWindow.Show();
                                this.Close();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error checking login: {ex.Message}");
            }
        }

        private async Task<bool> Login(string mobile, string password)
        {
            try
            {
                var loginUrl = "https://api.smaro.app/api/auth/client/login";
                var loginData = new { mobile, password };

                string jsonData = JsonConvert.SerializeObject(loginData);
                var content = new StringContent(jsonData, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(loginUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    string responseContent = await response.Content.ReadAsStringAsync();
                    var apiResponse = JsonConvert.DeserializeObject<ApiResponse>(responseContent);

                    if (apiResponse?.data?.user == null)
                    {
                        MessageBox.Show("Invalid login response from server.");
                        return false;
                    }

                    StoreAuthEntry(apiResponse.data.user);
                    MessageBox.Show("Login successful.");
                    return true;
                }
                else
                {
                    MessageBox.Show($"Login failed. Status code: {response.StatusCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during login: {ex.Message}");
                return false;
            }
        }

        private void StoreAuthEntry(AuthData authData)
        {
            try
            {
                using (var connection = _databaseHelper.GetConnection())
                {
                    connection.Open();

                    string insertQuery = @"
                        INSERT INTO auth (id, name, mobile, email, client_name, branch_name, client_id, branch_id,port) 
                        VALUES (@id, @name, @mobile, @email, @client_name, @branch_name, @client_id, @branch_id,@port)";

                    using (var command = new SQLiteCommand(insertQuery, connection))
                    {
                        command.Parameters.AddWithValue("@id", 1);  // Fixed ID for single-row storage
                        command.Parameters.AddWithValue("@name", authData.name);
                        command.Parameters.AddWithValue("@mobile", authData.mobile);
                        command.Parameters.AddWithValue("@email", authData.email);
                        command.Parameters.AddWithValue("@client_name", authData.client_name);
                        command.Parameters.AddWithValue("@branch_name", authData.branch_name);
                        command.Parameters.AddWithValue("@client_id", authData.client_id);
                        command.Parameters.AddWithValue("@branch_id", authData.client_branch_id);
                        command.Parameters.AddWithValue("@port", 11114);
                        

                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error storing authentication entry: {ex.Message}");
            }
        }

        // Show password in plain text
        private void ShowPasswordCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            PasswordTextBox.Text = PasswordBox.Password; // Transfer password to TextBox
            PasswordTextBox.Visibility = Visibility.Visible;
            PasswordBox.Visibility = Visibility.Collapsed;
        }

        // Hide password in plain text
        private void ShowPasswordCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            PasswordBox.Password = PasswordTextBox.Text; // Transfer text back to PasswordBox
            PasswordBox.Visibility = Visibility.Visible;
            PasswordTextBox.Visibility = Visibility.Collapsed;
        }
    }

    // Root response class with nested `data` field
    public class ApiResponse
    {
        public int statusCode { get; set; }
        public Data data { get; set; }
        public string message { get; set; }
    }

    public class Data
    {
        public AuthData user { get; set; }
        public string token { get; set; }
        public string expires_in { get; set; }
    }

    public class AuthData
    {
        public int id { get; set; }
        public string name { get; set; }
        public string mobile { get; set; }
        public string email { get; set; }
        public string client_name { get; set; }
        public string branch_name { get; set; }
        public int client_id { get; set; }
        public int client_branch_id { get; set; }
    }


}
