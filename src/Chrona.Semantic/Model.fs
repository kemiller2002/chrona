namespace Chrona.Semantic

[<Struct>]
type ActivityId = ActivityId of string

[<Struct>]
type Revision = Revision of int

type Activity =
    { Id: ActivityId
      BusinessDate: string
      ExactMinutes: int
      Description: string
      Revision: Revision }
