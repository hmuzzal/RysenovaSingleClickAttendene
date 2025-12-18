using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KravMagaWorker
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        private readonly string BasePath = @"C:\KravMagaBd";
        private readonly string CredFilePath;

        public Worker(ILogger<Worker> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;

            if (!Directory.Exists(BasePath))
                Directory.CreateDirectory(BasePath);

            CredFilePath = Path.Combine(BasePath, "kravmaga_cred.txt");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker started at {Time}", DateTime.Now);

            await EnsureCredentialFileAsync();
            await SendAttendanceForAllUsers();

            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }

        // =========================
        // CREDENTIAL HANDLING
        // =========================

        private async Task EnsureCredentialFileAsync()
        {
            if (!File.Exists(CredFilePath))
            {
                Console.WriteLine("Credential file not found. Creating new one...");
                await AddCredentialFromConsoleAsync();
                return;
            }

            var lines = await File.ReadAllLinesAsync(CredFilePath);
            if (lines.Length == 0)
            {
                Console.WriteLine("Credential file exists but empty.");
                await AddCredentialFromConsoleAsync();
            }
        }

        private async Task AddCredentialFromConsoleAsync()
        {
            Console.Write("Enter username: ");
            var username = Console.ReadLine();

            Console.Write("Enter password: ");
            var password = ReadPassword();

            var cred = new UserCredential
            {
                User_Name = username,
                Password = password
            };

            await File.AppendAllTextAsync(
                CredFilePath,
                JsonSerializer.Serialize(cred) + Environment.NewLine
            );

            Console.WriteLine("Credential saved successfully.\n");
        }

        private async Task UpdateCredentialAsync(UserCredential updated)
        {
            var lines = await File.ReadAllLinesAsync(CredFilePath);
            var updatedLines = new List<string>();

            foreach (var line in lines)
            {
                var cred = JsonSerializer.Deserialize<UserCredential>(line);
                if (cred.User_Name == updated.User_Name)
                    updatedLines.Add(JsonSerializer.Serialize(updated));
                else
                    updatedLines.Add(line);
            }

            await File.WriteAllLinesAsync(CredFilePath, updatedLines);
            Console.WriteLine("Credential updated successfully.\n");
        }

        private string ReadPassword()
        {
            var pwd = new StringBuilder();
            ConsoleKeyInfo key;

            while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter)
            {
                if (key.Key == ConsoleKey.Backspace && pwd.Length > 0)
                {
                    pwd.Remove(pwd.Length - 1, 1);
                    Console.Write("\b \b");
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    pwd.Append(key.KeyChar);
                    Console.Write("*");
                }
            }
            Console.WriteLine();
            return pwd.ToString();
        }

        // =========================
        // MAIN BUSINESS LOGIC
        // =========================

        private async Task SendAttendanceForAllUsers()
        {
            var lines = await File.ReadAllLinesAsync(CredFilePath);
            var client = _httpClientFactory.CreateClient();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var cred = JsonSerializer.Deserialize<UserCredential>(line,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (cred == null)
                        continue;

                    string token = await GetTokenForUser(client, cred);
                    if (string.IsNullOrEmpty(token))
                        continue;

                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", token);

                    // ---------------------------
                    // CURRENT SHIFT
                    // ---------------------------
                    var shiftResponse = await client.GetAsync(
                        "https://datavancedbd.rysenova.net/api/v1/employee/current-attendance-shift");

                    if (!shiftResponse.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("No attendance shift for {User}", cred.User_Name);
                        continue;
                    }

                    var shiftContent = await shiftResponse.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(shiftContent);

                    // ---------------------------
                    // ATTENDANCE TRACKING POLICY
                    // ---------------------------
                    var policyResponse = await client.GetAsync(
                        "https://datavancedbd.rysenova.net/api/v1/employee/attendance-tracking-policy?meta-key=web");

                    policyResponse.EnsureSuccessStatusCode();

                    var policyContent = await policyResponse.Content.ReadAsStringAsync();
                    using var policyDoc = JsonDocument.Parse(policyContent);

                    string attendanceTrackingPolicyId = policyDoc
                        .RootElement
                        .GetProperty("payload")[0]
                        .GetProperty("attendance_tracking_policy_id")
                        .GetString();

                    // ---------------------------
                    // ATTENDANCE PAYLOAD
                    // ---------------------------
                    var payload = doc.RootElement.GetProperty("payload");

                    if (payload.ValueKind != JsonValueKind.Array || payload.GetArrayLength() == 0)
                    {
                        _logger.LogWarning("No attendance data for {User}", cred.User_Name);
                        continue;
                    }

                    var attendance = payload[0];

                    string employeeId = attendance.GetProperty("employee_id").GetString();
                    string attendanceId = attendance.GetProperty("id").GetString();
                    string shift_id = attendance.GetProperty("shift_id").GetString();

                    // ---------------------------
                    // CHECK-IN FORM
                    // ---------------------------
                    using var form = new MultipartFormDataContent("----WebKitFormBoundaryCAhLqCJ6yiWaDjEu");
                    form.Add(new StringContent(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")), "in_time");
                    form.Add(new StringContent(attendanceTrackingPolicyId), "attendance_tracking_policy_id");
                    form.Add(new StringContent("false"), "is_mobile_device");
                    form.Add(new StringContent("PUT"), "_method");

                    string checkInUrl = $"https://datavancedbd.rysenova.net/api/v1/employee/check-in/{attendanceId}";

                    var request = new HttpRequestMessage(HttpMethod.Post, checkInUrl)
                    {
                        Content = form
                    };

                    var response = await client.SendAsync(request);
                    string responseContent = await response.Content.ReadAsStringAsync();

                    using var attendanceDoc = JsonDocument.Parse(responseContent);
                    bool success = attendanceDoc.RootElement.GetProperty("success").GetBoolean();
                    string message = attendanceDoc.RootElement.GetProperty("message").GetString();

                    if (success)
                        _logger.LogInformation("✅ Attendance success for {User}: {Msg}", cred.User_Name, message);
                    else
                        _logger.LogError("❌ Attendance failed for {User}: {Msg}", cred.User_Name, message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing user line: {Line}", line);
                }
            }
        }

        // =========================
        // TOKEN & LOGIN
        // =========================

        private async Task<string> GetTokenForUser(HttpClient client, UserCredential cred)
        {
            var tokenFilePath = Path.Combine(BasePath, $"token_{cred.User_Name}.txt");

            // Try cached token
            if (File.Exists(tokenFilePath))
            {
                var content = await File.ReadAllTextAsync(tokenFilePath);
                if (!string.IsNullOrEmpty(content))
                {
                    var stored = JsonSerializer.Deserialize<TokenStore>(content, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (stored != null && stored.Expiry > DateTime.UtcNow)
                    {
                        _logger.LogInformation("Using cached token for {User}, expires at {Expiry}", cred.User_Name, stored.Expiry);
                        return stored.AccessToken;
                    }
                }
            }

            // ---------------------------
            // LOGIN
            // ---------------------------
            var loginPayload = new { user_name = cred.User_Name, password = cred.Password };
            var json = JsonSerializer.Serialize(loginPayload);
            var loginContent = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("https://datavancedbd.rysenova.net/api/v1/login", loginContent);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"\nLogin failed for user: {cred.User_Name}");
                Console.WriteLine($"Stored username: {cred.User_Name}");
                Console.WriteLine($"Stored password: {"*".PadLeft(cred.Password.Length, '*')}");
                Console.Write("Do you want to update credentials? (Y/N): ");
                var choice = Console.ReadLine();

                if (choice?.Equals("Y", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // Prompt for new username
                    Console.Write($"Enter new username (or press Enter to keep '{cred.User_Name}'): ");
                    var newUsername = Console.ReadLine();
                    if (!string.IsNullOrWhiteSpace(newUsername))
                        cred.User_Name = newUsername;

                    // Prompt for new password
                    Console.Write("Enter new password (leave blank to keep current): ");
                    var newPassword = ReadPassword();
                    if (!string.IsNullOrWhiteSpace(newPassword))
                        cred.Password = newPassword;

                    await UpdateCredentialAsync(cred);
                    return await GetTokenForUser(client, cred); // Retry login
                }
                else
                {
                    Console.WriteLine("User chose not to update credentials. Exiting application.");
                    Environment.Exit(0); // Terminate the app gracefully
                }

                return null; // This line will never be reached, added to satisfy compiler
            }



            var loginResponse = JsonSerializer.Deserialize<LoginResponse>(responseBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var newTokenStore = new TokenStore
            {
                AccessToken = loginResponse?.Header?.AccessToken,
                Expiry = DateTime.UtcNow.AddHours(8) // or parse from Header.ExpiredAt if exists
            };

            await File.WriteAllTextAsync(tokenFilePath, JsonSerializer.Serialize(newTokenStore));
            _logger.LogInformation("New token saved for {User}, expires at {Expiry}", cred.User_Name, newTokenStore.Expiry);

            return newTokenStore.AccessToken;
        }

        // =========================
        // MODELS
        // =========================

        private class UserCredential
        {
            public string User_Name { get; set; }
            public string Password { get; set; }
        }

        private class TokenStore
        {
            public string AccessToken { get; set; }
            public DateTime Expiry { get; set; }
        }

        private class LoginResponse
        {
            public HeaderData Header { get; set; }
        }

        private class HeaderData
        {
            public string AccessToken { get; set; }
        }
    }
}
