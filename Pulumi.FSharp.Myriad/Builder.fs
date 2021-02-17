module AstBuilder

open AstOperations
open FSharp.Data
open AstInstance
open AstHelpers
open AstMember
open AstYield
open AstRun
open FsAst
open Core

open System.Text.RegularExpressions
open FSharp.Text.RegexProvider

// "azure:compute/virtualMachine:VirtualMachine"
// CloudProvider - Always the same for each schema (azure here)
type ResourceInfoProvider =
    Regex<"(?<CloudProvider>\w+):(?<ResourceProviderNamespace>[A-Za-z0-9.]+)/(?<SubNamespace>\w+):(?<ResourceType>\w+)">

type TypeInfoProvider =
    Regex<"(?<CloudProvider>\w+):(?<ResourceProviderNamespace>[A-Za-z0-9.]+)/(?<SubNamespace>\w+):(?<ResourceType>\w+)">

let resourceInfo =
    ResourceInfoProvider(RegexOptions.Compiled)

let typeInfo =
    TypeInfoProvider(RegexOptions.Compiled)
    
type BuilderType =
    | Type of TypeInfoProvider.MatchType
    | Resource of ResourceInfoProvider.MatchType

let private argIdent =
    Pat.ident("arg")
    
let private argToInput =
    Expr.func("input", "arg")
    
let private args =
    Expr.ident("args")
    
let private funcIdent =
    Expr.ident("func")
    
let private yieldReturnExpr =
    Expr.list([ Expr.ident("id") ])

let private matchExpr =
    Expr.paren(
        Expr.match'(Expr.tuple(Expr.ident("lName"), Expr.ident("rName")), [
            Match.clause(Pat.tuple(Pat.null', Pat.null'), Expr.null')
            Match.clause(Pat.tuple(Pat.null', Pat.ident("name")), Expr.ident("name"))
            Match.clause(Pat.tuple(Pat.ident("name"), Pat.null'), Expr.ident("name"))
            Match.clause(Pat.wild, Expr.failwith("Duplicate name"))
        ]))

let private combineExpr =
    Expr.tuple(matchExpr,
               Expr.paren(Expr.func("List.concat", (Expr.list [ "lArgs"; "rArgs" ]))))

let private combineArgs =
    Pat.paren (Pat.tuple ((Pat.paren (Pat.tuple ("lName", "lArgs"))),
                          (Pat.paren (Pat.tuple ("rName", "rArgs")))))
    
let private combineMember =
    createMember' "this" "Combine" [combineArgs.ToRcd] [] combineExpr
    
let private forArgs =
    Pat.paren (Pat.tuple ("args", "delayedArgs"))

let private forExpr =
    Expr.methodCall("this.Combine",
                    [ Expr.ident("args")
                      Expr.func("delayedArgs", Expr.unit) ])

let private forMember =
    createMember' "this" "For" [forArgs.ToRcd] [] forExpr
 
let private delayMember =
    createMember "Delay" [Pat.ident("f").ToRcd] [] (Expr.func("f"))

let private zeroMember =
    createMember "Zero" [Pat.wild.ToRcd] [] (Expr.unit)
    
let private yieldMember =
    createYield yieldReturnExpr
    
let private newNameExpr =
    Expr.tuple(Expr.ident("newName"),
               Expr.ident("args"))

let private nameMember =
    createNameOperation newNameExpr
    
let private identArgExpr =
    Expr.ident("arg")

let createYieldFor propName argsType =
    let setExpr =
        Expr.sequential([
            Expr.set("args." + propName, argToInput)
            args
        ])
    
    let expr =
        Expr.list([
            Expr.paren(
                Expr.sequential([
                    Expr.let'("func", [Pat.typed("args", argsType)], setExpr)
                    funcIdent
                ])
            )
        ])
    
    [ createYield' argIdent expr ]

let createBuilderClass allTypes isType name properties =
    let argsType =
        name + "Args"

    let apply =
        Expr.func("List.fold", [
            Expr.ident("func")
            Expr.paren(createInstance argsType Expr.unit)
            Expr.ident("args")
        ])
       
    let runArgs =
        if isType then
            apply
        else
            Expr.paren(
                Expr.tuple(
                    Expr.ident("name"),
                    Expr.paren(apply)
                ))
        
    let createOperations (propType : PTypeDefinition) =
        match propType with
        | { Type = PString }
        | { Type = PInteger }
        | { Type = PFloat }
        | { Type = PBoolean }
        | { Type = PArray _ }
        | { Type = PUnion _ }
        | { Type = PJson _ }
        | { Type = PMap _ }
        | { Type = PRef _; GenerateYield = false } // Why not generating operations even when generating Yield?
            -> createOperationsFor' isType propType.Name propType argsType
        | { Type = PAssetOrArchive _ }
        | { Type = PRef _ }
        | { Type = PAny _ }
        | { Type = PArchive _ }
            -> createYieldFor propType.Name argsType

    let nameAndType name (properties : (string * JsonValue) []) =
        let (|StartsWith|_|) (value : string) (text : string) =
            match text.StartsWith(value) with
            | true  -> String.length value |> text.Substring |> Some
            | false -> None

        let (|Property|_|) value seq =
            seq |> Seq.tryFind (fst >> ((=)value)) |> Option.map snd
        
        let typeMap =
            [ "string" , PString
              "number" , PFloat
              "integer", PInteger
              "boolean", PBoolean
              "Asset",   PAssetOrArchive
              "Any",     PAny
              "Archive", PArchive ] |> Map.ofList
        
        let rec getTypeInfo : ((string * JsonValue) []) -> PType =
            function
            | Property("type") (JsonValue.String("array")) &
              Property("items") (JsonValue.Record(itemType))
                -> getTypeInfo itemType |> PType.PArray
              
            | Property("type") (JsonValue.String("object")) &
              Property("additionalProperties") (JsonValue.Record(itemType))
                -> getTypeInfo itemType |> PType.PMap
              
            | Property("oneOf") (JsonValue.Array([| JsonValue.Record(one); JsonValue.Record(two) |]))
                -> PType.PUnion (getTypeInfo one, getTypeInfo two)
              
            | Property("$ref") (JsonValue.String(StartsWith("#/types/") typeQualified))
                -> PType.PRef typeQualified
              
            | Property("type") (JsonValue.String(baseType))
            | Property("$ref") (JsonValue.String(StartsWith("pulumi.json#/") baseType)) when (Map.containsKey baseType typeMap)
                -> typeMap.[baseType]
              
            | _ -> failwith ""
                
        let (Property("description") (JsonValue.String(description)), _) |
            (_, description) =
            properties, ""
            
        //let (Property("description") (JsonValue.String(description)), _) | (_, description) = properties, "Default description"
        
        let (Property("language") (JsonValue.Record((Property("csharp") (JsonValue.Record(Property("name") (JsonValue.String(name))))))), _) |
            (_, name) =
            properties, name |> toPascalCase
        
        let deprecation =
            match properties with
            | Property("deprecationMessage") (JsonValue.String(message)) -> Deprecated message
            | _                                                          -> Current
    
        let typeExists typeName =
            Array.contains typeName allTypes
        
        let simplifyWhenTypeDoesNotExist =
            function
            | PUnion (PRef refType, t)
            | PUnion (t, PRef refType) when not <| typeExists refType -> t
            | u -> u
        
        {
            Type = properties |> getTypeInfo |> simplifyWhenTypeDoesNotExist
            Description = description
            Name = name
            Deprecation = deprecation
            GenerateYield = true
        }
    
    let nameAndTypes =
        properties |>
        Array.map (fun (x, y : JsonValue) -> nameAndType x (y.Properties()))
        
    let (propOfSameComplexType, otherProperties) =
        nameAndTypes |>
        Array.groupBy (fun pt -> pt.Type) |>
        Array.partition (function | (PRef _, props) -> Array.length props > 1 | _ -> false) |>
        (fun (l, r) -> (l |> Array.collect snd,
                        r |> Array.collect snd))
        
    let propOfSameComplexTypeIgnoreComplex =
        propOfSameComplexType |>
        Array.map (fun td -> { td with GenerateYield = false })
        
    let order =
        nameAndTypes |> Array.map (fun td -> td.Name)
        
    let operations =
        Array.append propOfSameComplexTypeIgnoreComplex otherProperties |>
        Array.sortBy (fun n -> order |> Array.findIndex ((=)n.Name)) |>
        Seq.collect createOperations
        
    let runReturnExpr =
        Expr.sequential([
            Expr.let'("func", [ "args"; "f" ], Expr.func("f", "args"))
            if isType then runArgs else createInstance name runArgs
        ])
    
    Module.type'(name + "Builder", [
        Type.ctor()
        
        yieldMember
        createRun (if isType then null else "name") runReturnExpr
        combineMember
        forMember
        delayMember
        zeroMember
        
        yield! if isType then [] else [ nameMember ]
        
        yield! operations
    ])