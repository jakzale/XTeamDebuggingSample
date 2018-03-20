namespace FSharpLib

[<AbstractClass; Sealed>]
type Report private () =
    static member Report(science: int) =
        sprintf "Our current science yeild is %i" science

