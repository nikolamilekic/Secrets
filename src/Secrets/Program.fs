open System
open System.IO
open Argu
open Fs1PasswordConnect
open FSharpPlus
open Fleece.SystemTextJson
open FSharp.Data

[<RequireSubcommand>]
type Argument =
    | [<SubCommand; CliPrefix(CliPrefix.None)>] Get of ParseResults<GetArgument>
    | [<SubCommand; CliPrefix(CliPrefix.None)>] Inject of ParseResults<InjectArgument>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Get _ -> "Retrieves a secret from the Connect server"
            | Inject _ -> "Injects secrets into a file. Replacement strings must be of type {{ op://<VaultIdOrTitle/<ItemIdOrTitle>/<FieldIdOrLabel> }}"
and GetArgument =
    | [<MainCommand; ExactlyOnce>] Identifier of Identifier : string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Identifier _ -> "Replacement identifier of the item to retrieve in the form <VaultIdOrTitle/<ItemIdOrTitle>/<FieldIdOrLabel>"
and InjectArgument =
    | [<ExactlyOnce>] Template of TemplateFilePath : string
    | [<ExactlyOnce>] Output of OutputFilePath : string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Template _ -> "Template file path"
            | Output _ -> "Output file path"

let argv = Environment.GetCommandLineArgs()
let executingAssembly = argv.[0]
let settings =
    let settingsPath = Path.GetDirectoryName executingAssembly + "/SecretsSettings.json"
    let rawSettings = File.ReadAllText settingsPath
    match parseJson rawSettings with
    | Ok s -> s
    | Error e -> failwith $"Failed to decode SecretsSettings. Error: {e}"

let connectClient = ConnectClient.fromSettingsCached settings
let parser = ArgumentParser.Create(programName = "secrets")
let arguments =
    parser.Parse(inputs = (argv |> Array.skip 1), ignoreUnrecognized = false)

match arguments.GetSubCommand() with
| Get getArguments ->
    let identifier = getArguments.GetResult Identifier
    let injectString = "{{ op://" + identifier + " }}"

    let result = connectClient.Inject injectString |> Async.RunSynchronously
    match result with
    | Ok field -> printfn $"{field}"; exit 0
    | Error e -> printfn $"{e.ToString()}"; exit 1
| Inject injectArguments ->
    let templatePath = injectArguments.GetResult Template
    let outputPath = injectArguments.GetResult Output

    let template = File.ReadAllText templatePath
    let result = connectClient.Inject template |> Async.RunSynchronously
    match result with
    | Ok result ->
        File.WriteAllText(outputPath, result)
        exit 0
    | Error e -> printfn $"{e.ToString()}"; exit 1
