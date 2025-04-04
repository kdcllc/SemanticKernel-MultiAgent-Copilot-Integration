using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Authentication;

namespace caps.util;

public class EntraAppRegistration(ILogger<EntraAppRegistration> logger)
{
    private static readonly string AppName = "CopilotAgentPlugins";

    private readonly (string Id, string Value)[] _resourceAccessDetails =
    [
        // Delegated permissions from the image:
        // https://learn.microsoft.com/en-us/graph/permissions-reference#calendarsread
        ("465a38f9-76ea-45b9-9f34-9e8b0d4b0b42", "Calendars.Read"), // Read user calendars

        // https://learn.microsoft.com/en-us/graph/permissions-reference#calendarsreadwrite
        ("e1fe6dd8-ba31-4d61-89e7-88639da4683d", "Calendars.ReadWrite"), // Full access to user calendars

        // https://learn.microsoft.com/en-us/graph/permissions-reference#contactsread
        ("d56682ec-c09e-4743-aaf4-1a3aac4caa21", "Contacts.Read"), // Read user contacts

        // https://learn.microsoft.com/en-us/graph/permissions-reference#email
        ("64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0", "email"), // View users' email address

        // https://learn.microsoft.com/en-us/graph/permissions-reference#filesreadall
        ("df85f4d6-205c-4ac5-a5ea-6bf408dba283", "Files.Read.All"), // Read all files that user can access

        // https://learn.microsoft.com/en-us/graph/permissions-reference#mailread
        ("570282fd-fa5c-430d-a7fd-fc8dc98a9dca", "Mail.Read"), // Read user mail

        // https://learn.microsoft.com/en-us/graph/permissions-reference#mailreadwrite
        ("024d486e-b451-40bb-833d-3e66d98c5c73	", "Mail.ReadWrite"), // Read and write access to user mail

        // https://learn.microsoft.com/en-us/graph/permissions-reference#mailsend
        ("e383f46e-2787-4529-855e-0e479a3ffac0", "Mail.Send"), // Send mail as a user

        // https://learn.microsoft.com/en-us/graph/permissions-reference#tasksread
        ("f45671fb-e0fe-4b4b-be20-3d3ce43f1bcb", "Tasks.Read"), // Read user's tasks and task lists

        // https://learn.microsoft.com/en-us/graph/permissions-reference#tasksreadwrite
        ("2219042f-cab5-40cc-b0d2-16b1540b4c5f", "Tasks.ReadWrite"), // Create, read, update, and delete user's tasks and task lists

        // https://learn.microsoft.com/en-us/graph/permissions-reference#userread
        ("b340eb25-3456-403f-be2f-af7a0d370277", "User.Read") // Sign in and read user profile
    ];

    public async Task<AppRegistrationResult> CreateAppAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var graphClient = GetGraphClient(accessToken);

        var requiredResourceAccess = new RequiredResourceAccess
        {
            ResourceAppId = "00000003-0000-0000-c000-000000000000", // Microsoft Graph's App ID
            ResourceAccess = _resourceAccessDetails
                .Select(ra => new ResourceAccess
                {
                    Id = new Guid(ra.Id),
                    Type = "Scope" //"Role" for Application and "Scope" for Delegated permissions
                }).ToList()
        };

        var result = new AppRegistrationResult();

        var passwordCredential = new PasswordCredential
        {
            DisplayName = "ForSemanticKernel", // Match the description in the image
            StartDateTime = DateTime.UtcNow,
            EndDateTime = DateTime.UtcNow.AddYears(1) // Set expiration to 1 year from now
        };

        var application = new Application
        {
            DisplayName = AppName,
            SignInAudience = "AzureADMyOrg",
            PasswordCredentials = [passwordCredential],
            RequiredResourceAccess = [requiredResourceAccess],
            Web = new WebApplication
            {
                RedirectUris = ["http://localhost"], // Add the localhost redirect URI
                ImplicitGrantSettings = new ImplicitGrantSettings
                {
                    EnableAccessTokenIssuance = true, // Enable Access Tokens
                    EnableIdTokenIssuance = true     // Enable ID Tokens
                }
            },
            IsFallbackPublicClient = true // Enable public client flows
        };

        Application createdApplication = null;

        try
        {
            createdApplication = await graphClient.Applications.PostAsync(application);

            if (createdApplication == null)
            {
                return result;
            }
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
        {
            logger.LogError(ex, "Failed to create app");
            return result;
        }
        catch (System.Exception)
        {
            throw;
        }


        var appId = createdApplication.AppId ?? string.Empty;
        var secret = createdApplication?.PasswordCredentials?[0]?.SecretText ?? string.Empty;

        var spId = await CreateServicePrincipalAsync(appId, graphClient, cancellationToken);

        // var grantResult = await GrantRoleAsync(accessToken, spId, cancellationToken);

        result.AppId = appId;
        result.Secret = secret;
        result.ServicePrincipleId = spId;
        result.TenantId = await GetTenantIdAsync(accessToken, cancellationToken);

        // log all of the result properties
        logger.LogInformation("AppId: {AppId}", result.AppId);
        logger.LogInformation("Secret: {Secret}", result.Secret);
        logger.LogInformation("ServicePrincipleId: {ServicePrincipleId}", result.ServicePrincipleId);
        logger.LogInformation("TenantId: {TenantId}", result.TenantId);

        return result;
    }

    private async Task<string> GetTenantIdAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var graphClient = GetGraphClient(accessToken);

        try
        {
            var organization = await graphClient.Organization.GetAsync(cancellationToken: cancellationToken);
            return organization?.Value?.FirstOrDefault()?.Id ?? string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get tenant id");
            return string.Empty;
        }
    }

    public async Task<IEnumerable<string>> GrantRoleAsync(
        string accessToken,
        string principalId,
        CancellationToken cancellationToken)
    {
        var graphClient = GetGraphClient(accessToken);

        var result = new List<string>();

        // Step 1: Get the appRoles of the resource service principal
        var sps = await graphClient.ServicePrincipals.GetAsync((requestConfiguration) =>
        {
            requestConfiguration.QueryParameters.Filter = "displayName eq 'Microsoft Graph'";
            requestConfiguration.QueryParameters.Select = ["id", "displayName", "appId", "appRoles"];
        }, cancellationToken: cancellationToken);

        var resourceServicePrincipal = sps?.Value?.FirstOrDefault();
        if (resourceServicePrincipal == null)
        {
            result.Add("Failed to find resource service principal");
            return result;
        }

        foreach (var (Id, Value) in _resourceAccessDetails)
        {
            try
            {
                var appRoleId = resourceServicePrincipal?.AppRoles?.FirstOrDefault(role => role.Value == Value)?.Id;
                if (appRoleId != null)
                {
                    var requestBody = new AppRoleAssignment
                    {
                        PrincipalId = Guid.Parse(principalId),
                        ResourceId = Guid.Parse(resourceServicePrincipal?.Id!),
                        AppRoleId = appRoleId.Value,
                    };

                    var appRoleAssignment = await graphClient.ServicePrincipals[resourceServicePrincipal?.Id].AppRoleAssignedTo.PostAsync(requestBody, cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Grant Admin on failed for: {Value}");
                result.Add($"Grant Admin on failed for: {Value}-{ex.Message}");
            }
        }

        return result;
    }

    public async Task<string> GetServicePrincipalByAppIdAsync(string accessToken, string appId, CancellationToken cancellationToken)
    {
        var graphClient = GetGraphClient(accessToken);

        var result = await graphClient.ServicePrincipals.GetAsync((requestConfiguration) =>
        {
            requestConfiguration.QueryParameters.Filter = $"appId eq '{appId}'";
            requestConfiguration.QueryParameters.Select = ["id", "displayName", "appId", "appRoles"];
        }, cancellationToken: cancellationToken);

        var servicePrincipal = result?.Value?.FirstOrDefault()?.Id;
        return servicePrincipal ?? string.Empty;
    }

    public async Task<string> GetServicePrincipleAsync(string accessToken, CancellationToken cancellationToken)
    {
        var graphClient = GetGraphClient(accessToken);

        var result = await graphClient.ServicePrincipals.GetAsync((requestConfiguration) =>
        {
            requestConfiguration.QueryParameters.Filter = $"displayName eq '{AppName}'";
            requestConfiguration.QueryParameters.Select = ["id", "displayName", "appId", "appRoles"];
        }, cancellationToken: cancellationToken);

        var resourceServicePrincipal = result?.Value?.FirstOrDefault()?.Id;
        return resourceServicePrincipal ?? string.Empty;
    }

    public async Task<bool> DeleteAppAsync(string appId, string accessToken, CancellationToken cancellationToken)
    {
        var graphClient = GetGraphClient(accessToken);

        try
        {
            var app = await graphClient.Applications.GetAsync(requestConfiguration =>
            {
                requestConfiguration.QueryParameters.Filter = "appId eq '" + appId + "'";
            });

            var oId = app?.Value?.FirstOrDefault();
            if (oId == null)
            {
                return false;
            }

            await graphClient.Applications[oId.Id].DeleteAsync(cancellationToken: cancellationToken);
            return true;
        }
        catch (ServiceException ex)
        {
            logger.LogError(ex, "Failed to delete app");
            return false;
        }
    }

    private GraphServiceClient GetGraphClient(string accessToken)
    {
        var authenticationProvider = new BaseBearerTokenAuthenticationProvider(new AccessTokenProvider(accessToken));
        return new GraphServiceClient(authenticationProvider);
    }

    private async Task<string> CreateServicePrincipalAsync(
        string appId,
        GraphServiceClient graphClient,
        CancellationToken cancellationToken)
    {
        try
        {
            var servicePrincipal = new ServicePrincipal
            {
                AppId = appId
            };

            var result = await graphClient.ServicePrincipals.PostAsync(servicePrincipal, cancellationToken: cancellationToken);

            return result?.Id ?? string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create service principal");
            return string.Empty;
        }
    }
}
