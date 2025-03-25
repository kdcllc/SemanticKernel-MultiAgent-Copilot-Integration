using System.Diagnostics;
using System.Text.Json;

namespace caps.util;

public class AzAccessTokenFetcher
{
    public static string GetAccessToken()
    {
        try
        {
            // Execute the az command
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "az",
                    Arguments = "account get-access-token",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            // Read the output
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new Exception($"Error executing az command: {error}");
            }

            // Parse the JSON response
            var jsonDocument = JsonDocument.Parse(output);
            if (jsonDocument.RootElement.TryGetProperty("accessToken", out var accessToken))
            {
                return accessToken.GetString() ?? string.Empty;
            }

            throw new Exception("accessToken not found in the response.");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to fetch access token: {ex.Message}", ex);
        }
    }
}