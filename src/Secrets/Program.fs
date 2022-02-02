open System
open System.IO
open Argu
open Fs1PasswordConnect
open FSharpPlus
open Milekic.YoLo

[<RequireSubcommand>]
type Argument =
    | [<SubCommand; CliPrefix(CliPrefix.None)>] Get of ParseResults<GetArgument>
    | [<SubCommand; CliPrefix(CliPrefix.None)>] Inject of ParseResults<InjectArgument>
    | [<SubCommand>] Version
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Get _ -> "Retrieves a secret from the Connect server"
            | Inject _ -> "Injects secrets into a file. Replacement strings must be of type {{ op://<VaultIdOrTitle/<ItemIdOrTitle>/<FieldIdOrLabel> }}"
            | Version -> "Prints the version and exits"
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
let connectClient =
    lazy
    ConnectClient.fromEnvironmentVariablesCached ()
    |> Result.failOnError "Failed to retrieve credentials from environment variables. You must set OP_CONNECT_HOST and OP_CONNECT_TOKEN."

let parser = ArgumentParser.Create(programName = "secrets")
let arguments =
    try parser.Parse(inputs = (argv |> Array.skip 1), ignoreUnrecognized = false)
    with e -> printfn $"{e.Message}"; exit 1

match arguments.GetSubCommand() with
| Version -> printfn $"{Metadata.productDescription.Value}"; exit 0
| Get getArguments ->
    let identifier = getArguments.GetResult Identifier
    let injectString = "{{ op://" + identifier + " }}"

    let result = connectClient.Value.Inject injectString |> Async.RunSynchronously
    match result with
    | Ok field -> printfn $"{field}"; exit 0
    | Error e -> printfn $"{e.ToString()}"; exit 1
| Inject injectArguments ->
    let templatePath = injectArguments.GetResult Template
    let outputPath = injectArguments.GetResult Output

    let template = File.ReadAllText templatePath
    let result = connectClient.Value.Inject template |> Async.RunSynchronously
    match result with
    | Ok result ->
        File.WriteAllText(outputPath, result)
        exit 0
    | Error e -> printfn $"{e.ToString()}"; exit 1
