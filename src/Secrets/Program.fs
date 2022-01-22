open System
open System.IO
open Argu
open Milekic.YoLo
open FSharpPlus
open FSharpPlus.Lens
open FSharp.Data
open Fleece.SystemJson

[<RequireSubcommand>]
type Argument =
    | [<SubCommand; CliPrefix(CliPrefix.None)>] Get of ParseResults<GetArgument>
    | [<SubCommand; CliPrefix(CliPrefix.None)>] Inject of ParseResults<InjectArgument>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Get _ -> "Retrieves a secret from the Connect server"
            | Inject _ -> "Injects secrets into a file. Replacement strings must be of type {{ connect://<ItemIdOrName>/<FieldLabelOrId> }}"
and GetArgument =
    | [<ExactlyOnce>] Item of ItemIdOrName : string
    | [<ExactlyOnce>] Field of FieldLabelOrId : string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Item _ -> "Name of the item to retrieve, or its ID."
            | Field _ -> "Label of the field to retrieve, or its ID"
and InjectArgument =
    | [<ExactlyOnce>] Template of TemplateFilePath : string
    | [<ExactlyOnce>] Output of OutputFilePath : string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Template _ -> "Template file path"
            | Output _ -> "Output file path"

type Settings = {
    Host : string
    Token : string
}

let inline _host f s = s.Host |> f <&> fun v -> { s with Host = v }
let inline _token f s = s.Token |> f <&> fun v -> { s with Token = v }

let settingsCodec =
    fun h t -> { Host = h; Token = t }
    |> withFields
    |> jfield "Host" (view _host)
    |> jfield "Token" (view _token)
    |> Codec.compose jsonObjToValueCodec
    |> Codec.compose jsonValueToTextCodec

let argv = Environment.GetCommandLineArgs()
let executingAssembly = argv.[0]
let settings =
    let settingsPath = Path.GetDirectoryName executingAssembly + "/SecretsSettings.json"
    let rawSettings = File.ReadAllText settingsPath
    match Codec.decode settingsCodec rawSettings with
    | Ok s -> s
    | Error e -> failwith $"Failed to decode SecretsSettings. Error: {e}"

let headers = [
    "Authorization", "Bearer " + settings.Token
]

let basePath = settings.Host + "/v1"

let parser = ArgumentParser.Create(programName = "secrets")
let arguments =
    parser.Parse(inputs = (argv |> Array.skip 1), ignoreUnrecognized = false)

[<Literal>]
let VaultsSampleResponse = """[
  {
    "id": "id",
    "name": "name",
    "attributeVersion": 1,
    "contentVersion": 11,
    "type": "USER_CREATED"
  }
]"""
type VaultsResponse = JsonProvider<VaultsSampleResponse>

[<Literal>]
let ItemsSampleResponse = """[
  {
    "id": "item id",
    "title": "item title",
    "tags": [
      "LastPass Import 9-19-20"
    ],
    "version": 3,
    "vault": {
      "id": "vault it"
    },
    "category": "LOGIN",
    "lastEditedBy": "last edited id",
    "urls": [
      {
        "primary": true,
        "href": "https://www.google.com/"
      }
    ]
  }
]"""
type ItemsResponse = JsonProvider<ItemsSampleResponse>

[<Literal>]
let ItemSampleResponse = """ {
  "id": "item id",
  "title": "item title",
  "tags": [
    "LastPass Import 9-19-20"
  ],
  "version": 3,
  "vault": {
    "id": "vault id"
  },
  "category": "LOGIN",
  "lastEditedBy": "last edited id",
  "sections": [
    {
      "id": "section id"
    },
    {
      "id": "linked items",
      "label": "Related Items"
    }
  ],
  "fields": [
    {
      "id": "field id",
      "type": "STRING",
      "purpose": "purpose",
      "label": "label",
      "value": "value"
    }
  ],
  "urls": [
    {
      "primary": true,
      "href": "https://www.google.com"
    }
  ]
}
"""
type ItemResponse = JsonProvider<ItemSampleResponse>

let getVaults () =
    Http.RequestString (
        $"{basePath}/vaults",
        headers = headers
    )
    |> VaultsResponse.Parse

let getVaultItems =
    let mutable cache = Map.empty
    fun (id : string) ->
        match Map.tryFind id cache with
        | Some items -> items
        | None ->
            let items =
                Http.RequestString (
                    $"{basePath}/vaults/{id}/items",
                    headers = headers
                )
                |> ItemsResponse.Parse
            cache <- Map.add id items cache
            items

let getItem =
    let mutable cache = Map.empty
    fun (itemHeader : ItemsResponse.Root) ->
        match Map.tryFind itemHeader.Id cache with
        | Some item -> item
        | None ->
            let item =
                Http.RequestString (
                    $"{basePath}/vaults/{itemHeader.Vault.Id}/items/{itemHeader.Id}",
                    headers = headers
                )
                |> ItemResponse.Parse
            cache <- Map.add itemHeader.Id item cache
            item

let getField
    (vaults : VaultsResponse.Root[])
    (requestedItem : string)
    (requestedField : string) =
    let requestedItem = requestedItem.ToLower()
    let requestedField = requestedField.ToLower()

    let items = query {
        for _vault in vaults do
        let _vaultItems = getVaultItems _vault.Id
        for vaultItem in _vaultItems do
        where (vaultItem.Id.ToLower() = requestedItem ||
            vaultItem.Title.ToLower() = requestedItem)
        select vaultItem
    }

    let itemHeaderMaybe = items |> Seq.tryHead
    if itemHeaderMaybe |> Option.isNone then failwith $"Item {requestedItem} not found"

    let itemHeader = itemHeaderMaybe.Value
    let item = getItem itemHeader

    let fields = query {
        for field in item.Fields do
        where (field.Id.ToLower() = requestedField || field.Label.ToLower() = requestedField)
        select field
    }

    let fieldMaybe = fields |> Seq.tryHead
    if fieldMaybe |> Option.isNone then failwith $"Field {requestedField} not found"

    fieldMaybe.Value.Value

match arguments.GetSubCommand() with
| Get getArguments ->
    let requestedItem = getArguments.GetResult Item
    let requestedField = getArguments.GetResult Field

    let vaults = getVaults ()
    let field = getField vaults requestedItem requestedField
    printfn $"{field}"
| Inject injectArguments ->
    let templatePath = injectArguments.GetResult Template
    let outputPath = injectArguments.GetResult Output

    let vaults = getVaults ()
    let template = File.ReadAllText templatePath
    let rec processTemplate template =
        match template with
        | Regex "{{ connect://(.+)/(.+) }}" [ item; field ] ->
            let replacement = getField vaults item field
            template.Replace("{{ connect://" + $"{item}/{field}" + " }}", replacement)
            |> processTemplate
        | _ -> template
    let output = processTemplate template
    File.WriteAllText(outputPath, output)
