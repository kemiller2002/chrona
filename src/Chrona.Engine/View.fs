/// The shape of what the engine projects to the page: Limen's view state.
///
/// A view is a flat list of named values. A value is a scalar or a list of
/// items whose fields are scalars; Limen cannot represent nested lists, so
/// neither can this type.
module Chrona.Engine.View

type Scalar =
    | Text of string
    | Flag of bool
    | Number of float

type ViewValue =
    | Value of Scalar
    | Items of (string * Scalar) list list

type View = (string * ViewValue) list
