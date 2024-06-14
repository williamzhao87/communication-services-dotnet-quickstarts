|page_type|languages|products
|---|---|---|
|sample|<table><tr><td>csharp</tr></td></table>|<table><tr><td>azure</td><td>azure-communication-services</td></tr></table>|

# Call Automation Recording using Media Streaming Sample

## Overview
This sample demonstrates a sample end-to-end flow using Azure Communication Services Call Automation SDK 
to answer an incoming call and start recording using media streaming.

## Flow
![Flow](./images/unmixed_demo.png)

## Prerequisites
- Create an Azure account with an active subscription. For details, see [Create an account for free](https://azure.microsoft.com/free/)
- [Visual Studio (2022 17.4)](https://visualstudio.microsoft.com/vs/) and above
- Enable (Visual Studio Web Tunnels) option in Visual Studio [https://devblogs.microsoft.com/visualstudio/public-preview-of-dev-tunnels-in-visual-studio-for-asp-net-core-projects/]
- [.NET 7](https://dotnet.microsoft.com/en-us/download/dotnet/7.0) and above
- Create an Azure Communication Services resource. For details, see [Create an Azure Communication Resource](https://docs.microsoft.com/azure/communication-services/quickstarts/create-communication-resource). You'll need to record your resource connection string for this sample.
- Create an [Azure Storage resource](https://azure.microsoft.com/en-us/products/cognitive-services/)

## Before running the sample for the first time
1. Open an instance of PowerShell, Windows Terminal, Command Prompt or equivalent and navigate to the directory that you'd like to clone the sample to.
2. git clone https://github.com/williamzhao87/Communication-Services-dotnet-quickstarts.git.

## How to run locally
1. Go to CallAutomation_RecordingStreaming folder and open `RecordingStreaming.sln` solution in Visual Studio.
2. Start your service and copy down the CALLBACK URI which will be printed out to the console log (this is using Visual Studio Web Tunnel)
3. Fillout the following properties in UserSecrets:
```
{
  "CallbackUri": "https://...ngrok.io",
  "WebsocketUri": "wss://.../ws",
  "ACSConnectionString": "<YOUR_ACS_CONN_STRING>",
  "StorageConnectionString": "<YOUR_STORAGE_STRING>",
  "BotMri": "<8_ACS_MRI>",
  "PauseOnStart": "false",
  "Kusto": {
    "IngestionUri": "<KUSTO_INGEST_URI>",
    "DatabaseName": "<KUSTO_DB>",
    "TableName": "<KUSTO_TABLE>"
  }
}
```
4. Setup the following [Event Grid subscriptions](https://learn.microsoft.com/en-us/azure/event-grid/event-schema-communication-services) for your ACS resource in the Azure Portal
  - Incoming Call with Webhook Uri `<CALLBACK URI>/api/incomingCall`