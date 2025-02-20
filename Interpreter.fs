module Interpreter
    open Parsec
    open FSharp.Core
    #nowarn "40"

    type Expression = 
        | Atom of string
        | Applicative of Expression * Expression
        | Lambda of Expression * Expression 
    and 'a Output = 
        | Value of 'a
        | Failed of string
    and Envirement = list<string * Expression>

    
    let parseExpr = 
        let rec parseAtom =
            Parser {
                let! atom = ['a'..'z'] |> Seq.toList |> anyOf |> many 1 
                return atom
            } <?> "Atom" |>> (toString >> Atom)
        and parseApp = 
            Parser {
                let pParens p = between (expect '(') p (expect ')')
                let! app = pParens ( parseExpression .>> pSpaces .>>. parseExpression )
                return app 
            } <?> "Applicative" |>> Applicative
        and parseLambda = 
            Parser {
                let pLmbda = anyOf [ '\\'; 'λ']; 
                let pDot = expect '.'
                let! lmbda = pLmbda >>. parseAtom .>> pDot .>>. parseExpression 
                return lmbda
            } <?> "Lambda" |>> fun (param,body) -> Lambda (param ,body)
        and parseExpression  = 
            Parser {
                let! expr = 
                    choice [    
                        parseAtom     
                        parseApp        
                        parseLambda 
                    ] 
                return expr
            } <?> "Expression" 
        parseExpression
    
    let evalExpr input= 
        match input with
        | Parsec.Parser.Success (term,_) -> 
            let rec occurs identifier = 
                function
                | Atom id -> 
                    let isThere = id = identifier
                    (isThere, isThere)
                | Lambda (arg, body) -> 
                    let inArg = arg = Atom(identifier)
                    let inBody = (identifier, body) ||> occurs |> fst 
                    (inArg || inBody, inArg && inBody)
                | Applicative(lhs, rhs) -> 
                    let isThere = (identifier,lhs) ||> occurs |> fst || (identifier,rhs) ||> occurs |> fst 
                    (isThere, isThere)
            let rec ``α Convert`` id lambda=  
                let rec convert old rep expr = 
                    let convert' = convert old rep
                    match expr with 
                    | Atom(id) -> 
                        if id = old then Atom rep
                        else expr
                    | Applicative   (lhs, rhs)   -> Applicative (convert' lhs, convert' rhs)
                    | Lambda        (arg, body)  -> Lambda      (arg,convert' body)
                match occurs id lambda with 
                | (true,_) -> 
                   Failed (sprintf "New name '%A' already appears in %A" id lambda)
                | _ -> match lambda with 
                       | Lambda (Atom(arg),body) -> Lambda (Atom(id), convert arg id body) |> Value 
                       | _ -> Failed (sprintf "α-conversion not supported for %A" lambda)
            let (/>) = ``α Convert``

            let rec ``β-redex`` =
                let allVariables expr =
                    let rec loop expr : seq<string> =
                        seq {
                            match expr with
                                | Atom name -> yield name
                                | Applicative (func, arg) ->
                                    yield! loop func
                                    yield! loop arg
                                | Lambda (param, body) ->
                                    yield match param with  | Atom(name) -> name 
                                                            | _ -> failwith "not a valid parameter for Lambda"
                                    yield! loop body
                        }
                    loop expr |> Set.ofSeq
                let rec substitute arg param body =
                    let substitute' = substitute arg param  
                    match body with
                    | Atom id ->
                        if id = param then Value arg
                        else Value body 
                    | Applicative(fn, arg) -> 
                        match substitute' fn, substitute' arg  with 
                        | Value(fn'), Value(arg') ->  Value (Applicative(fn',arg'))
                        | (Failed(msg) ,_) | (_, Failed (msg)) -> Failed msg 
                    | Lambda(Atom(local), body') -> 
                        if local = param then Value body 
                        else 
                            let occurence = (local, arg)
                                            ||> occurs
                                             |> snd
                            if occurence then 
                                let localVars = allVariables body
                                let result = ['a'..'z']
                                             |> Seq.map (fun c -> c.ToString())
                                             |> Seq.tryFind (not << localVars.Contains)
                                             |> Option.map (fun repl -> 
                                                 match repl /> body with
                                                 | Value v -> substitute' <| v 
                                                 | Failed _ as error ->  error )
                                match result with 
                                | Some v -> v
                                | _ -> Failed "Exhausted variable names for α-conversion"
                            else
                                match substitute' body' with 
                                | Value body'' -> Lambda (Atom(local), body'') |> Value
                                | Failed _ as error -> error
                    | _ -> Failed "β-redex : Subtitution Failed"  
                function
                | Applicative (Lambda (Atom(param), body), arg) ->
                    substitute arg param body
                | expression -> Failed <| sprintf "%A is not a β-redex" expression
            let (</) () expr= ``β-redex`` expr

            let evaluate expression = 
                let rec isBetaRedex = 
                    function
                    | Atom _ -> false
                    | Applicative (Lambda(_), _) -> true
                    | Applicative (lhs, rhs) -> 
                        isBetaRedex lhs || isBetaRedex rhs
                    | Lambda (_, body) ->
                        isBetaRedex body
                let rec reduce expr = 
                    match expr with 
                    | Atom _ -> Value expr
                    | Applicative (Lambda(_), _) -> 
                        () </ expr 
                    | Lambda (arg, body) ->
                        match reduce body with 
                        | Value body' -> 
                            Lambda (arg, body') |> Value 
                        | error -> error
                    | Applicative(lhs, rhs) ->
                        let lhsc,rhsc = isBetaRedex lhs,isBetaRedex rhs 
                        match lhsc,rhsc with 
                        | (true, _) -> 
                            match reduce lhs with 
                            | Value v ->  Applicative (v, rhs) |> Value
                            | error -> error
                        | (_, true) -> 
                            match reduce rhs with 
                            | Value v ->  Applicative (lhs, v) |> Value
                            | error -> error
                        | _ -> Value expr
                let rec loop expr =
                    match isBetaRedex expr with 
                    | true ->   expr 
                                |>  reduce 
                                |>  function 
                                    | Value expr' -> loop expr'
                                    | error -> error
                    | _ -> Value expr
                loop expression                          
            evaluate term
        | error -> error |> toResult |> Failed  

    let toString expr = 
        match expr with 
        | Value v ->
            let rec toString' term = 
                match term with 
                | Lambda(arg,body) -> 
                    match arg with 
                    | Atom(name) -> sprintf "λ%s.%s" name (toString' body)
                    | _ -> failwith "invalid Lambda parameter"
                | Atom(v) -> sprintf "%s" v
                | Applicative(lhs,rhs) -> sprintf "(%s %s)" (toString' lhs) (toString' rhs)
            toString' v
        | Failed e -> e
        
    let interpret input = 
        (fromStr input, parseExpr .>> eof) 
            ||> run 
             |> evalExpr
             |> toString
