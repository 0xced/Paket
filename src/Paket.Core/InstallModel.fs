﻿namespace Paket

open System.IO

[<RequireQualifiedAccess>]
type Reference =
| Library of string
| FrameworkAssemblyReference of string

type InstallFiles = 
    { References : Reference Set
      ContentFiles : string Set }
    
    static member empty = 
        { References = Set.empty
          ContentFiles = Set.empty }
    
    member this.AddReference lib = { this with References = Set.add (Reference.Library lib) this.References }
    member this.AddFrameworkAssemblyReference assemblyName = { this with References = Set.add (Reference.FrameworkAssemblyReference assemblyName) this.References }

type InstallModel = 
    { PackageName : string
      PackageVersion : SemVerInfo
      Frameworks : Map<FrameworkIdentifier, InstallFiles>
      Fallbacks : Map<string, InstallFiles> }

    static member EmptyModel(packageName, packageVersion) : InstallModel = 
        let frameworks = 
            [ for x in FrameworkVersion.KnownDotNetFrameworks -> DotNetFramework(x) ]
        { PackageName = packageName
          PackageVersion = packageVersion
          Fallbacks = Map.empty
          Frameworks = List.fold (fun map f -> Map.add f InstallFiles.empty map) Map.empty frameworks }
    
    member this.GetFrameworks() = this.Frameworks |> Seq.map (fun kv -> kv.Key)

    member this.GetFrameworkGroups() =
        this.Frameworks
        |> Seq.groupBy (fun kv -> kv.Key.GetFrameworkIdentifier())

    member this.GetFiles(framework) = 
        match this.Frameworks.TryFind framework with
        | Some x -> 
            x.References
            |> Seq.map (fun x ->
                match x with
                | Reference.Library lib -> Some lib
                | _ -> None)
            |> Seq.choose id
        | None -> Seq.empty

    member this.GetReferences(framework) = 
        match this.Frameworks.TryFind framework with
        | Some x -> x.References
        | None -> Set.empty

    member this.AddReference(framework,lib:string,references) : InstallModel = 
        let install =
            match references with
            | NuspecReferences.All -> true
            | NuspecReferences.Explicit list -> list |> List.exists lib.EndsWith

        if not install then this else
        { this with Frameworks = 
                     match Map.tryFind framework this.Frameworks with
                     | Some files -> Map.add framework (files.AddReference lib) this.Frameworks
                     | None -> Map.add framework (InstallFiles.empty.AddReference lib) this.Frameworks }

    member this.AddReferences(libs,references) : InstallModel =         
        libs |> Seq.fold (fun model lib -> 
                    match FrameworkIdentifier.DetectFromPath lib with
                    | Some framework -> model.AddReference(framework,lib,references)
                    | _ -> model) this

    member this.AddReferences(libs) = this.AddReferences(libs, NuspecReferences.All)

    member this.AddFrameworkAssemblyReference(framework,assemblyName) : InstallModel = 
        { this with Frameworks = 
                     match Map.tryFind framework this.Frameworks with
                     | Some files -> Map.add framework (files.AddFrameworkAssemblyReference assemblyName) this.Frameworks
                     | None -> Map.add framework (InstallFiles.empty.AddFrameworkAssemblyReference assemblyName) this.Frameworks }

    member this.AddFrameworkAssemblyReferences(references) : InstallModel  = 
        references
        |> Seq.fold (fun model reference -> model.AddFrameworkAssemblyReference(reference.TargetFramework,reference.AssemblyName)) this

    member this.FilterBlackList() =
        let blackList =
            [fun (reference:Reference) -> 
                match reference with
                | Reference.Library lib -> not (lib.EndsWith ".dll" || lib.EndsWith ".exe")
                | _ -> false]

        { this with Frameworks = 
                     blackList 
                     |> List.fold 
                            (fun frameworks f ->  Map.map (fun _ files -> {files with References = files.References |> Set.filter (f >> not)}) frameworks) 
                        this.Frameworks }

    member this.UseLowerVersionLibIfEmpty() = 
        FrameworkVersion.KnownDotNetFrameworks
        |> List.rev
        |> List.fold (fun (model : InstallModel) (lowerVersion) -> 
               let newFiles = model.GetReferences(DotNetFramework(lowerVersion))
               if Set.isEmpty newFiles then model
               else 
                   FrameworkVersion.KnownDotNetFrameworks
                   |> List.filter (fun (version) -> (version) > (lowerVersion))
                   |> List.fold (fun (model : InstallModel) (upperVersion) -> 
                          let framework = DotNetFramework(upperVersion)
                          match Map.tryFind framework model.Frameworks with
                          | Some files when Set.isEmpty files.References -> 
                              { model with Frameworks = Map.add framework { References = newFiles; ContentFiles = Set.empty } model.Frameworks }
                          | _ -> model) model) this

    member this.UseLastInGroupAsFallback() =
        this.Frameworks 
        |> Seq.fold 
            (fun (model : InstallModel) kv -> 
                let group = kv.Key.GetFrameworkIdentifier()

                { model with Fallbacks = Map.add group kv.Value model.Fallbacks}) this


    member this.UsePortableVersionLibIfEmpty() = 
        this.Frameworks 
        |> Seq.fold 
            (fun (model : InstallModel) kv -> 
                let newFiles = kv.Value.References
           
                if Set.isEmpty newFiles then model else

                let otherProfiles = 
                    match kv.Key with
                    | PortableFramework(_, f) -> 
                        f.Split([| '+' |], System.StringSplitOptions.RemoveEmptyEntries)
                        |> Array.map FrameworkIdentifier.Extract
                        |> Array.choose id
                    | _ -> [||]

                if Array.isEmpty otherProfiles then model else 
                otherProfiles 
                |> Array.fold (fun (model : InstallModel) framework -> 
                    match Map.tryFind framework model.Frameworks with
                    | Some files when Set.isEmpty files.References |> not -> model
                    | _ -> { model with Frameworks = Map.add framework { References = newFiles; ContentFiles = Set.empty} model.Frameworks }) model) this

    member this.Process() =
        this
            .UseLowerVersionLibIfEmpty()
            .UsePortableVersionLibIfEmpty()
            .UseLowerVersionLibIfEmpty()  // because we now might need to use portable            
            .FilterBlackList()
            .UseLastInGroupAsFallback()

    member this.GetLibraryNames =
        lazy([ for f in this.Frameworks do
                for lib in f.Value.References do         
                    match lib with
                    | Reference.Library lib ->       
                        let fi = new FileInfo(normalizePath lib)
                        yield fi.Name.Replace(fi.Extension,"") 
                    | _ -> ()]
            |> Set.ofList)

    static member CreateFromLibs(packageName,packageVersion,libs,nuspec:Nuspec) = 
        InstallModel.EmptyModel(packageName,packageVersion)
            .AddReferences(libs,nuspec.References)
            .AddFrameworkAssemblyReferences(nuspec.FrameworkAssemblyReferences)
            .Process()