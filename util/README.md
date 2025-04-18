# Create Microsoft Entra App Registration to be used with Copilot Agent Plugins

Azure CLI must be installed and device flow used with an account that allows to create apps.

```bash
    az login --use-device-code
```

[Manually register the app](https://github.com/microsoft/semantic-kernel/blob/c669f74099629db40c281397886ae5d81856e9e4/dotnet/samples/Demos/CopilotAgentPlugins/README.md)

## Delegated Permissions:

[Microsoft Graph permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference)

1. **Calendars.Read** - Read user calendars
2. **Calendars.ReadWrite** - Have full access to user calendars
3. **Contacts.Read** - Read user contacts
4. **email** - View users' email address
5. **Files.Read.All** - Read all files that the user can access
6. **Mail.Read** - Read user mail
7. **Mail.ReadWrite** - Read and write access to user mail
8. **Mail.Send** - Send mail as a user
9. **Tasks.Read** - Read user's tasks and task lists
10. **Tasks.ReadWrite** - Create, read, update, and delete user's tasks and task lists
11. **User.Read** - Sign in and read user profile

These permissions are all **delegated** and have been granted for the application.