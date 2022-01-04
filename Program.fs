module Program

open Argu
open DotNet.Globbing
open System
open System.IO
open System.Text.Json
open System.Threading

open Args
open Types

let defaultGlob = "**/*"

let withColor color f =
    Console.ForegroundColor <- color
    f ()
    Console.ResetColor()

let printWithColor color s =
    withColor color (fun () -> printfn "%s" s)

let note verbose s =
    if verbose then
        "Note: " + s |> printWithColor ConsoleColor.Blue

let warn s =
    "Warning: " + s
    |> printWithColor ConsoleColor.Yellow

let err s =
    "Error: " + s |> printWithColor ConsoleColor.Red

let warnMissingGames (games: list<string>) config =
    let warningPrinted =
        List.fold
            (fun warningPrinted game ->
                if not (Array.exists (fun g -> g.Name = game) config.Games) then
                    warn $"No game named `%s{game}'"
                    true
                else
                    warningPrinted)
            false
            games

    if warningPrinted then printfn ""

let printConfigRow label value newValue =
    printfn "%s: %s%s" label value
    <| match newValue with
       | Some nv when value = nv -> ""
       | Some nv -> $" -> %s{nv}"
       | None -> ""

let printGame game newName newPath newGlob =
    printConfigRow "Name" game.Name newName
    printConfigRow "Save path" game.Path newPath

    match (game.Glob, newGlob) with
    | Some _, _
    | None, Some _ -> printConfigRow "Save glob" (Option.defaultValue "" game.Glob) newGlob
    | _ -> ()

    printfn ""

let cleanupBackups (backupPath: string) verbose config =
    if config.NumToKeep > 0 then
        let glob =
            Glob.Parse(
                backupPath
                + ".bak.[0-9][0-9][0-9][0-9]_[0-9][0-9]_[0-9][0-9]_[0-9][0-9]_[0-9][0-9]_[0-9][0-9]"
            )

        let allFiles =
            Directory.EnumerateFiles(Path.GetDirectoryName(backupPath))

        let files =
            Seq.filter (fun f -> glob.IsMatch(f: string)) allFiles
            |> Seq.append (seq { backupPath })
            |> Seq.toList

        if (List.length files > config.NumToKeep) then
            let sortedFiles =
                List.sortByDescending File.GetLastWriteTimeUtc files

            let filesToDelete = List.skip config.NumToKeep sortedFiles

            for file in filesToDelete do
                note verbose $"Deleting %s{file}"
                File.Delete(file)

let rec backupFile game basePath glob fromPath toPath verbose config =
    try
        let globMatches () =
            let glob =
                Glob.Parse(Path.Join(basePath, Option.defaultValue defaultGlob glob))

            glob.IsMatch(fromPath: string)

        let copyAndCleanup () =
            Directory.CreateDirectory(Path.GetDirectoryName(toPath: string))
            |> ignore

            printfn $"%s{fromPath} ==>\n\t%s{toPath}"
            File.Copy(fromPath, toPath)
            cleanupBackups toPath verbose config
            (1, [])

        let backupFile' () =
            let fromInfo = FileInfo(fromPath)

            let fromIsReparsePoint =
                fromInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)

            if fromIsReparsePoint then
                note verbose $"%s{fromPath} appears to be a link to somewhere else in the filesystem. Skipping..."
                (0, [])
            else
                let fromModTime = fromInfo.LastWriteTimeUtc

                let toModTime =
                    if File.Exists(toPath) then
                        Some(File.GetLastWriteTimeUtc(toPath))
                    else
                        None

                match toModTime with
                | Some toModTime ->
                    if fromModTime <> toModTime then
                        File.Move(
                            toPath,
                            toPath
                            + ".bak."
                            + toModTime.ToString("yyyy_MM_dd_HH_mm_ss")
                        )

                        copyAndCleanup ()
                    else
                        (0, [])
                | None -> copyAndCleanup ()

        if Directory.Exists(fromPath) then
            backupFiles game basePath glob fromPath toPath verbose config
        else if globMatches () then
            backupFile' ()
        else
            (0, [])
    with
    | e ->
        let warning =
            $"Unable to backup file %s{toPath} for game %s{game}:\n\
                %s{e.Message}\n"

        warn warning
        (1, [ warning ])

and backupFiles game basePath glob fromPath toPath verbose config =
    Directory.EnumerateFileSystemEntries(fromPath)
    |> Seq.fold
        (fun (c, es) path ->
            let file = Path.GetFileName(path)

            let newCount, newErrs =
                backupFile game basePath glob (Path.Join(fromPath, file)) (Path.Join(toPath, file)) verbose config

            (c + newCount, es @ newErrs))
        (0, [])

let backupGame gameName verbose config =
    let startTime = DateTime.Now

    let game =
        Array.tryFind (fun g -> g.Name = gameName) config.Games

    match game with
    | Some game ->
        if Directory.Exists game.Path then
            let backedUpCount, warnings =
                backupFiles game.Name game.Path game.Glob game.Path (Path.Join(config.Path, gameName)) verbose config

            if (backedUpCount > 0) then
                let now = DateTime.Now
                let warningCount = List.length warnings

                printfn
                    "\nFinished backing up %d file%s%s for %s in %fs on %s at %s\n"
                    backedUpCount
                    (if backedUpCount = 1 then "" else "s")
                    (if warningCount > 0 then
                         sprintf " with %d warning%s" warningCount (if warningCount = 1 then "" else "s")
                     else
                         "")
                    gameName
                    (now - startTime).TotalSeconds
                    (now.ToLongDateString())
                    (now.ToLongTimeString())

            warnings
        else
            warn $"Path set for %s{gameName} doesn't exist: %s{game.Path}"
            []
    | None ->
        warnMissingGames [ gameName ] config
        []

let rec backup (gameNames: option<list<string>>) (loop: bool) (verbose: bool) config =
    let gameNames' =
        match gameNames with
        | None -> Array.map (fun g -> g.Name) config.Games
        | Some gns -> gns |> List.toArray

    let warnings =
        gameNames'
        |> Array.fold
            (fun acc game ->
                try
                    let warnings = backupGame game verbose config
                    acc @ warnings
                with
                | e ->
                    err $"Error backing up %s{game}: %s{e.Message}"
                    acc)
            []

    let warningCount = List.length warnings

    if warningCount > 0 then
        withColor ConsoleColor.Yellow (fun () ->
            printf "\n%d warning%s occurred:" warningCount (if warningCount = 1 then "" else "s")

            if verbose then
                printfn "\n"
                List.iter (printfn "%s") warnings
            else
                printfn "\nPass --verbose flag to print all warnings after backup completes\n")

    if loop then
        Thread.Sleep(TimeSpan.FromMinutes(float config.Frequency))
        backup gameNames loop verbose config
    else
        None

let validGameNameChars: Set<char> =
    [ 'A' .. 'Z' ]
    @ [ 'a' .. 'z' ] @ [ '0' .. '9' ] @ [ '-'; '_' ]
    |> Set.ofList

let isValidGameName (name: string) =
    String.forall (fun c -> Set.contains c validGameNameChars) name

let absolutePath (path: string) =
    if path.Length > 0 && path.[0] = '~' then
        let home =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)

        Path.Join(home, path.[1..]) |> Path.GetFullPath
    else
        Path.GetFullPath path

let add (game: string) (path: string) (glob: option<string>) config =
    if Array.exists (fun g -> g.Name = game) config.Games then
        err $"Game with the name %s{game} already exists"
        None
    elif not (isValidGameName game) then
        err $"Invalid characters in name `%s{game}': only alphanumeric characters, underscores, and hyphens are allowed"
        None
    else
        let newGame =
            { Name = game
              Path = absolutePath path
              Glob = glob }

        let newGames =
            Array.append config.Games [| newGame |]
            |> Array.sortBy (fun g -> g.Name)

        printfn "Game added successfully:\n"
        printGame newGame None None None
        Some { config with Games = newGames }

let list config =
    Array.iter (fun g -> printfn $"%s{g.Name}") config.Games
    None

let info (gameNames: option<list<string>>) config =
    match gameNames with
    | Some gns -> warnMissingGames gns config
    | None -> ()

    let games =
        match gameNames with
        | None -> config.Games
        | Some gs -> Array.filter (fun g -> List.contains g.Name gs) config.Games

    Array.iter (fun g -> printGame g None None None) games
    None

let rec promptYorN prompt =
    stdout.Flush()
    printf $"%s{prompt} (y/N) "

    match Console.ReadLine().Trim().ToLower() with
    | "y" -> true
    | "n"
    | "" -> false
    | _ -> promptYorN prompt

let remove (games: list<string>) (yes: bool) config =
    warnMissingGames games config

    let newGames =
        [| for game in config.Games do
               if List.contains game.Name games
                  && (yes
                      || promptYorN (
                          "Are you sure you want to remove "
                          + game.Name
                          + "?"
                      )) then
                   printfn $"Removed %s{game.Name}"
               else
                   yield game |]

    Some { config with Games = newGames }

let edit (gameName: string) (name: option<string>) (path: option<string>) (glob: option<string>) config =
    match (name, path, glob) with
    | None, None, None ->
        err "One or more of --name, --path, or --glob must be provided."
        None
    | _ ->
        let splitList =
            Array.tryFindIndex (fun g -> g.Name = gameName) config.Games
            |> Option.map (fun i -> config.Games |> Array.toList |> List.splitAt i)

        match splitList with
        | None ->
            warnMissingGames [ gameName ] config
            None
        | Some (_, []) ->
            err "Couldn't find game in list"
            None
        | Some (front, game :: back) ->
            let newName = Option.defaultValue game.Name name

            let newGlobForSave =
                match glob with
                | Some ""
                | Some "none" -> None
                | glob -> glob

            let newGlobForPrint =
                Option.map
                    (function
                    | "none" -> ""
                    | glob -> glob)
                    glob

            let newPath =
                Option.defaultValue game.Path path |> absolutePath

            let editedGame =
                { Name = newName
                  Path = newPath
                  Glob = newGlobForSave }

            if not (isValidGameName newName) then
                err
                    $"Invalid characters in name `%s{newName}': only alphanumeric characters, underscores, and hyphens are allowed"

                None
            else
                printGame game (Some newName) (Some newPath) newGlobForPrint

                let backupDirExists =
                    Directory.Exists(Path.Join(config.Path, gameName))

                if (Option.isSome name && backupDirExists) then
                    warn "Game name changed, renaming backup directory..."
                    Directory.Move(Path.Join(config.Path, gameName), Path.Join(config.Path, newName))

                Some { config with Games = front @ editedGame :: back |> List.toArray }

let printConfig config newBackupDir newBackupFreq newBackupsToKeep =
    printConfigRow "Backup path" config.Path newBackupDir
    printConfigRow "Backup frequency (in minutes)" (string config.Frequency) (Option.map string newBackupFreq)
    printConfigRow "Number of backups to keep" (string config.NumToKeep) (Option.map string newBackupsToKeep)
    printfn ""

let editConfig (backupDir: option<string>) (backupFreq: option<int>) (backupsToKeep: option<int>) config =
    let newBackupDir =
        Option.defaultValue config.Path backupDir
        |> absolutePath

    let newBackupFreq =
        Option.defaultValue config.Frequency backupFreq

    let newBackupsToKeep =
        Option.defaultValue config.NumToKeep backupsToKeep

    printConfig config (Some newBackupDir) (Some newBackupFreq) (Some newBackupsToKeep)

    match (backupDir, backupFreq, backupsToKeep) with
    | None, None, None -> None
    | _ ->
        Some
            { config with
                Path = newBackupDir
                Frequency = newBackupFreq
                NumToKeep = newBackupsToKeep }

let defaultConfigPath =
    Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sbu", "config.json")

let defaultConfig =
    { Path = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sbu-backups")
      Frequency = 15
      NumToKeep = 20
      Games = [||] }

let saveDefaultConfig path =
    printfn
        $"Creating new config file at `%s{path}'.\n\
        Use the `config' command to update default values, which are:\n\
        \n\
        Backup path: %s{defaultConfig.Path}\n\
        Backup frequency (in minutes): %d{defaultConfig.Frequency}\n\
        Number of backups to keep: %d{defaultConfig.NumToKeep}\n"

    match Path.GetDirectoryName(path: string) with
    | "" -> ()
    | path -> Directory.CreateDirectory(path) |> ignore

    File.WriteAllText(path, JsonSerializer.Serialize(defaultConfig))

let app (parseResults: ParseResults<_>) =
    let command = parseResults.GetSubCommand()

    match command with
    | Backup sp -> backup (sp.TryGetResult BackupArgs.Games) (sp.Contains Loop) (sp.Contains Verbose)
    | Add sp -> add (sp.GetResult AddArgs.Game) (sp.GetResult AddArgs.Path) (sp.TryGetResult AddArgs.Glob)
    | List _ -> list
    | Info sp -> info (sp.TryGetResult InfoArgs.Games)
    | Remove sp -> remove (sp.GetResult Games) (sp.Contains Yes)
    | Edit sp -> edit (sp.GetResult Game) (sp.TryGetResult Name) (sp.TryGetResult EditArgs.Path) (sp.TryGetResult Glob)
    | Config sp -> editConfig (sp.TryGetResult Path) (sp.TryGetResult Frequency) (sp.TryGetResult Keep)
    | ConfigPath _
    | Version -> failwithf $"non-command matched as command: %A{command}"

let loadConfig configPath =
    if not (File.Exists(configPath)) then
        saveDefaultConfig configPath

    try
        configPath
        |> File.ReadAllText
        |> JsonSerializer.Deserialize<Config>
    with
    | e ->
        warn
            $"Couldn't load config: %s{e.Message}\nAttempting to save default config \
            to '%s{configPath}' after backing up existing config.\n"

        if File.Exists(configPath) then
            File.Copy(configPath, configPath + ".bak", true)

        saveDefaultConfig configPath
        defaultConfig

[<EntryPoint>]
let main argv =
    try
        let parser =
            ArgumentParser.Create<SbuArgs>(programName = AppDomain.CurrentDomain.FriendlyName)

        let parseResults =
            parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)

        if parseResults.Contains Version then
            printfn "sbu v1.3.0"
        else
            let configPath =
                parseResults.TryGetResult ConfigPath
                |> Option.defaultValue defaultConfigPath

            let config = loadConfig configPath
            let newConfig = app parseResults config

            match newConfig with
            | None -> ()
            | Some c ->
                match Path.GetDirectoryName(configPath).Trim() with
                | "" -> ()
                | configDir -> Directory.CreateDirectory(configDir) |> ignore

                File.WriteAllText(configPath, JsonSerializer.Serialize(c))
    with
    | :? ArguParseException as e -> printfn $"%s{e.Message}"
    | e -> err e.Message

    0
