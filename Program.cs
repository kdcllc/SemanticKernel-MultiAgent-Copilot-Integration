﻿﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using caps.util;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
//using Microsoft.SemanticKernel.Agents.History;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using Microsoft.SemanticKernel.Plugins.OpenApi;
using Microsoft.SemanticKernel.Plugins.OpenApi.Extensions;

internal class Program
{
    private static IConfiguration? _configuration;
    private static ILogger<Program>? _logger;
    private static BearerAuthenticationProviderWithCancellationToken? _bearerAuthenticationProviderWithCancellationToken;
    
    private static async Task Main(string[] args)
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
        });

        // var accessToken = AzAccessTokenFetcher.GetAccessToken();
        // var entra = new EntraAppRegistration(loggerFactory.CreateLogger<EntraAppRegistration>());
        // var app = await entra.CreateAppAsync(accessToken, cancellationToken: default);
        // return;

        var root = Directory.GetCurrentDirectory();
        var dotenv = Path.Combine(root, ".env");
        DotEnv.Load(dotenv);

        // Initialize configuration and logging
        var configurationBuilder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables();

        _configuration = configurationBuilder.Build();

        var list = _configuration.AsEnumerable().ToList();


        _logger = loggerFactory.CreateLogger<Program>();
        var bearerLogger = loggerFactory.CreateLogger<BearerAuthenticationProviderWithCancellationToken>();

        _bearerAuthenticationProviderWithCancellationToken = new BearerAuthenticationProviderWithCancellationToken(_configuration, bearerLogger);

        // Initialize the kernel
        var kernel = await InitializeKernelAsync();

        // Load plugins dynamically
        await LoadPluginsAsync(kernel);

        Console.WriteLine("Defining agents...");

        // Define Agent Names
        const string ChiefOfStaffName = "ChiefOfStaffAgent";
        const string ContactsName = "ContactsAgent";
        const string CalendarName = "CalendarAgent";
        const string EmailName = "EmailAgent";
        const string LegalSecretaryName = "LegalSecretaryAgent";

        // Define Chief of Staff Agent
        ChatCompletionAgent chiefOfStaffAgent = new()
        {
            Name = ChiefOfStaffName,
            Instructions =
                """
                You are the Chief of Staff Agent, responsible for overseeing and orchestrating AI-powered interactions.
                Your goal is to ensure that user queries are **interpreted accurately** and **routed to the appropriate agent**.

                - When a user provides a prompt, you analyze its intent.
                - You assign the task to the appropriate agent (Contacts, Calendar, or Mail).
                - Once an agent refines the request, you review it to ensure it aligns with the user's original intent.
                - You engage in **back-and-forth iteration** with the specialized agents to ensure **accuracy** and **clarity**.
                - You confirm when an agent's refined request is **ready for execution**.

                **Rules:**
                - Always verify the agent's modifications against the original user prompt.
                - Ensure the final request aligns with **API plugin specifications**.
                - Continue iterations until you and the agent reach **agreement**.
                """,
            Kernel = kernel,
            Arguments = new KernelArguments(
                new AzureOpenAIPromptExecutionSettings()
                {
                    //FunctionChoiceBehavior = FunctionChoiceBehavior.Required(null)
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
                })
        };

        // Define Specialized Agents (Contacts, Calendar, Email)
        ChatCompletionAgent contactsAgent = new()
        {
            Name = ContactsName,
            Instructions =
                """
                You are the Contacts Agent, responsible for ensuring **contact-related queries** conform to the Contacts API specifications.
                Your role is to validate, refine, and optimize queries for retrieving contacts.
                """,
            Kernel = kernel,
        };

        ChatCompletionAgent calendarAgent = new()
        {
            Name = CalendarName,
            Instructions =
                """
                You are the Calendar Agent, ensuring **calendar-related queries** adhere to the Microsoft Graph Calendar API specifications.
                Your job is to validate and refine calendar queries before execution.
                """,
            Kernel = kernel,
        };

        ChatCompletionAgent emailAgent = new()
        {
            Name = EmailName,
            Instructions =
                """
                You are the Email Agent, ensuring **email-related queries** conform to the Microsoft Graph Mail API.
                Your role is to validate, refine, and optimize queries for sending or retrieving emails.
                """,
            Kernel = kernel,
        };

        // Define Legal Secretary Agent
        ChatCompletionAgent legalSecretaryAgent = new()
        {
            Name = LegalSecretaryName,
            Instructions =
                """
                You are the Legal Secretary Agent. Your role is to ensure that responses are:
                - Free from **bank account information, Social Security numbers, or ID numbers**.
                - Written in **proper English** with **clear and professional wording**.
                - **Translated into French** at the bottom for multinational teams.

                **Rules:**
                - **If you find any restricted information, redact it immediately.**
                - **If the English text is unclear or incorrect, rewrite it for clarity.**
                - **At the end of each response, provide a French translation.**

                **Example Response Format:**
                - **English Response**: (Corrected content here)
                - **French Translation**: (Translated content here)
                """,
            Kernel = kernel
        };


        // Define Selection Strategy (Which Agent Speaks Next?)
        KernelFunction selectionFunction =
            AgentGroupChat.CreatePromptFunctionForStrategy(
                $$$"""
                Examine the provided RESPONSE and choose the next participant.
                State only the name of the chosen participant without explanation.
                Never choose the participant named in the RESPONSE.

                Choose only from these participants:
                - {{{ContactsName}}}
                - {{{CalendarName}}}
                - {{{EmailName}}}
                - {{{LegalSecretaryName}}}
                - {{{ChiefOfStaffName}}}

                Always follow these rules when choosing the next participant:
                - If RESPONSE is user input, analyze the message:
                    - If it contains words like **"contact"**, **"phone number"**, **"address book"**, choose {{{ContactsName}}}.
                    - If it contains words like **"calendar"**, **"meeting"**, **"event"**, choose {{{CalendarName}}}.
                    - If it contains words like **"email"**, **"inbox"**, **"send mail"**, choose {{{EmailName}}}.
                - If RESPONSE is by a specialized agent (Contacts, Calendar, or Email), the **next step MUST be the {{{LegalSecretaryName}}} **.
                - If RESPONSE is by LegalSecretaryAgent, return to the Chief of Staff Agent.
                - If the topic is unclear, default to the Chief of Staff Agent.

                RESPONSE:
                {{$lastmessage}}
                """,
                safeParameterNames: "lastmessage"
            );




        // Define Termination Strategy (When to Stop)
        const string TerminationToken = "yes";

        KernelFunction terminationFunction =
            AgentGroupChat.CreatePromptFunctionForStrategy(
                $$$"""
                Examine the RESPONSE and provide at least 1 suggestion the first pass
                The RESPONSE must have both an English AND French version at the end. 
                Then determine whether the content has been deemed satisfactory.
                If content is satisfactory, respond with a single word without explanation: {{{TerminationToken}}}.
                If specific suggestions are being provided, it is not satisfactory.
                If no correction is suggested, it is satisfactory.

                RESPONSE:
                {{$lastmessage}}
                """,
                safeParameterNames: "lastmessage"
            );

        // Limit chat history to recent message (Optimize Token Usage)
        ChatHistoryTruncationReducer historyReducer = new(1);

        // Define the Agent Group Chat
        AgentGroupChat chat =
            new(chiefOfStaffAgent, contactsAgent, calendarAgent, emailAgent, legalSecretaryAgent)
            {
                ExecutionSettings = new AgentGroupChatSettings
                {
                    SelectionStrategy =
                        new KernelFunctionSelectionStrategy(selectionFunction, kernel)
                        {
                            // Always start with the Chief of Staff Agent
                            InitialAgent = chiefOfStaffAgent,
                            // Optimize token usage
                            HistoryReducer = historyReducer,
                            // Set prompt variable for tracking
                            HistoryVariableName = "lastmessage",
                            // Extract agent name from result
                            //ResultParser = (result) => result.GetValue<string>() ?? chiefOfStaffAgent.Name
                            ResultParser = (result) =>
                            {
                                var selectedAgent = result.GetValue<string>() ?? chiefOfStaffAgent.Name;
                                Console.WriteLine($"🔍 Debug: Selection Strategy chose {selectedAgent}");
                                return selectedAgent;
                            }
                        },
                    TerminationStrategy =
                        new KernelFunctionTerminationStrategy(terminationFunction, kernel)
                        {
                            // Evaluate only for Chief of Staff responses
                            Agents = [chiefOfStaffAgent],
                            // Optimize token usage
                            HistoryReducer = historyReducer,
                            // Set prompt variable for tracking
                            HistoryVariableName = "lastmessage",
                            // Limit total turns to avoid infinite loops
                            MaximumIterations = 5,
                            // Determines if the process should exit
                            ResultParser = (result) =>
                                result.GetValue<string>()?.Contains(TerminationToken, StringComparison.OrdinalIgnoreCase) ?? false
                        }
                }
            };

        await PromptLoopAsync(kernel, chat);
    }

    private static async Task<Kernel> InitializeKernelAsync()
    {
        var kernelBuilder = Kernel.CreateBuilder();

        var openAIConfig = _configuration?.GetSection("OpenAI");
        var apiKey = openAIConfig?["ApiKey"];
        var modelId = openAIConfig?["ModelId"];

        var azureOpenAIConfig = _configuration?.GetSection("AzureOpenAI");
        var endpoint = azureOpenAIConfig?["Endpoint"];
        var azureApiKey = azureOpenAIConfig?["ApiKey"];
        var modelDeploymentName = azureOpenAIConfig?["ChatDeploymentName"];

        // Add OpenAI Chat Completion service
        if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(modelId))
        {
           kernelBuilder.AddOpenAIChatCompletion(modelId: modelId, apiKey: apiKey);
        }
        else
        {
            kernelBuilder.AddAzureOpenAIChatCompletion(
                deploymentName: modelDeploymentName,
                endpoint: endpoint,
                apiKey: azureApiKey
            );
        }
        
        return kernelBuilder.Build();
    }

        private static async Task LoadPluginsAsync(Kernel kernel)
    {
        const string PluginsDirectory = "Plugins/CopilotAgentPlugins";
        if (!Directory.Exists(PluginsDirectory))
        {
            _logger?.LogWarning($"Plugins directory not found: {PluginsDirectory}");
            return;
        }

        foreach (var pluginPath in Directory.GetDirectories(PluginsDirectory))
        {
            var pluginName = Path.GetFileName(pluginPath);
            var manifestFile = Directory.GetFiles(pluginPath, "*-apiplugin.json").FirstOrDefault();

            if (string.IsNullOrEmpty(manifestFile))
            {
                _logger?.LogWarning($"No manifest file found for plugin: {pluginName}. Ensure a file ending with '-apiplugin.json' exists in {pluginPath}.");
                continue;
            }

            try
            {
                var copilotAgentPluginParameters = new CopilotAgentPluginParameters
                {
                    FunctionExecutionParameters = new()
                    {
                        { "https://graph.microsoft.com/v1.0", new OpenApiFunctionExecutionParameters(authCallback: Program._bearerAuthenticationProviderWithCancellationToken.AuthenticateRequestAsync, enableDynamicOperationPayload: false, enablePayloadNamespacing: true) { ParameterFilter = s_restApiParameterFilter} }
                        
                    },
                };
                // Convert manifest path to absolute path
                var manifestPath = Path.GetFullPath(manifestFile);

                if (!File.Exists(manifestPath))
                {
                    _logger?.LogError($"Manifest file not found: {manifestPath}");
                    continue;
                }

                _logger?.LogInformation($"Loading plugin '{pluginName}' from {manifestPath}...");

                // Load the plugin using the correct method
                await kernel.ImportPluginFromCopilotAgentPluginAsync(pluginName, manifestPath, copilotAgentPluginParameters);
                _logger?.LogInformation($"Plugin '{pluginName}' loaded successfully.");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Failed to load plugin '{pluginName}' from {manifestFile}.");
            }
        }
    }
    
    private static async Task PromptLoopAsync(Kernel kernel, AgentGroupChat chat)
    {
        Console.WriteLine("Ready!");

        bool isComplete = false;
        do
        {
            Console.WriteLine();
            Console.Write("How may I help you? > ");
            string input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }
            input = input.Trim();
            if (input.Equals("EXIT", StringComparison.OrdinalIgnoreCase))
            {
                isComplete = true;
                break;
            }

            if (input.Equals("RESET", StringComparison.OrdinalIgnoreCase))
            {
                await chat.ResetAsync();
                Console.WriteLine("[Conversation has been reset]");
                continue;
            }

            // Add user input to the chat history
            chat.AddChatMessage(new ChatMessageContent(AuthorRole.User, input));

            chat.IsComplete = false;

            try
            {
                Console.WriteLine("🟡 Debug: Invoking chat...");
                await foreach (ChatMessageContent response in chat.InvokeAsync())
                {
                    Console.WriteLine($"🟢 Debug: {response.AuthorName} responded");
                    Console.WriteLine($"{response.AuthorName.ToUpperInvariant()}:{Environment.NewLine}{response.Content}");

                    // ✅ Explicitly check if a specialized agent is responding
                    if (response.AuthorName.Equals("ContactsAgent", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("🔍 Debug: ContactsAgent is processing this request.");
                    }
                    else if (response.AuthorName.Equals("CalendarAgent", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("🔍 Debug: CalendarAgent is processing this request.");
                    }
                    else if (response.AuthorName.Equals("EmailAgent", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("🔍 Debug: EmailAgent is processing this request.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error in chat execution: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine(ex.InnerException.Message);
                }
            }
        } while (!isComplete);
    }


    #region MagicDoNotLookUnderTheHood
    private static readonly HashSet<string> s_fieldsToIgnore = new(
        [
            "@odata.type",
            "attachments",
            "bccRecipients",
            "bodyPreview",
            "categories",
            "ccRecipients",
            "conversationId",
            "conversationIndex",
            "extensions",
            "flag",
            "from",
            "hasAttachments",
            "id",
            "inferenceClassification",
            "internetMessageHeaders",
            "isDeliveryReceiptRequested",
            "isDraft",
            "isRead",
            "isReadReceiptRequested",
            "multiValueExtendedProperties",
            "parentFolderId",
            "receivedDateTime",
            "replyTo",
            "sender",
            "sentDateTime",
            "singleValueExtendedProperties",
            "uniqueBody",
            "webLink",
        ],
        StringComparer.OrdinalIgnoreCase
    );
    private const string RequiredPropertyName = "required";
    private const string PropertiesPropertyName = "properties";
    /// <summary>
    /// Trims the properties from the request body schema.
    /// Most models in strict mode enforce a limit on the properties.
    /// </summary>
    /// <param name="schema">Source schema</param>
    /// <returns>the trimmed schema for the request body</returns>
    private static KernelJsonSchema? TrimPropertiesFromRequestBody(KernelJsonSchema? schema)
    {
        if (schema is null)
        {
            return null;
        }

        var originalSchema = JsonSerializer.Serialize(schema.RootElement);
        var node = JsonNode.Parse(originalSchema);
        if (node is not JsonObject jsonNode)
        {
            return schema;
        }

        TrimPropertiesFromJsonNode(jsonNode);

        return KernelJsonSchema.Parse(node.ToString());
    }
    private static void TrimPropertiesFromJsonNode(JsonNode jsonNode)
    {
        if (jsonNode is not JsonObject jsonObject)
        {
            return;
        }
        if (jsonObject.TryGetPropertyValue(RequiredPropertyName, out var requiredRawValue) && requiredRawValue is JsonArray requiredArray)
        {
            jsonNode[RequiredPropertyName] = new JsonArray(requiredArray.Where(x => x is not null).Select(x => x!.GetValue<string>()).Where(x => !s_fieldsToIgnore.Contains(x)).Select(x => JsonValue.Create(x)).ToArray());
        }
        if (jsonObject.TryGetPropertyValue(PropertiesPropertyName, out var propertiesRawValue) && propertiesRawValue is JsonObject propertiesObject)
        {
            var properties = propertiesObject.Where(x => s_fieldsToIgnore.Contains(x.Key)).Select(static x => x.Key).ToArray();
            foreach (var property in properties)
            {
                propertiesObject.Remove(property);
            }
        }
        foreach (var subProperty in jsonObject)
        {
            if (subProperty.Value is not null)
            {
                TrimPropertiesFromJsonNode(subProperty.Value);
            }
        }
    }
#pragma warning disable SKEXP0040
    private static readonly RestApiParameterFilter s_restApiParameterFilter = (RestApiParameterFilterContext context) =>
    {
#pragma warning restore SKEXP0040
        if ("me_sendMail".Equals(context.Operation.Id, StringComparison.OrdinalIgnoreCase) &&
            "payload".Equals(context.Parameter.Name, StringComparison.OrdinalIgnoreCase))
        {
            context.Parameter.Schema = TrimPropertiesFromRequestBody(context.Parameter.Schema);
            return context.Parameter;
        }
        return context.Parameter;
    };
    private sealed class ExpectedSchemaFunctionFilter : IAutoFunctionInvocationFilter
    {//TODO: this eventually needs to be added to all CAP or DA but we're still discussing where should those facilitators live
        public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
        {
            await next(context).ConfigureAwait(false);

            if (context.Result.ValueType == typeof(RestApiOperationResponse))
            {
                var openApiResponse = context.Result.GetValue<RestApiOperationResponse>();
                if (openApiResponse?.ExpectedSchema is not null)
                {
                    openApiResponse.ExpectedSchema = null;
                }
            }
        }
    }
    #endregion
}