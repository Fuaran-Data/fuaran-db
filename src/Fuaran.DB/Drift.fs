namespace Fuaran.DB

open Fuaran.Core

/// One table as a source reported it: its physical name and its columns with their types.
type ReportedTable =
    {
        /// The source's identifier for the table.
        Name: string
        /// The columns, in the source's order, by physical name.
        Columns: Schema
    }

/// A schema as a source reported it — data, from whatever reported it. Nothing here asks a source;
/// the host that owns the connection reads the catalogue and hands the answer over.
type ReportedSchema =
    {
        /// The tables, in the source's order.
        Tables: ReportedTable list
    }

/// The drift verdict for one table.
[<RequireQualifiedAccess>]
type TableDrift =
    /// The model and the source agree on the table's columns, types and order.
    | InStep of table: string
    /// The model and the source disagree; the delta is `Schema.diff model reported`, in physical
    /// names, so `Added` is what the source holds that the model does not.
    | Drifted of table: string * delta: SchemaDelta
    /// The model holds the table and the source did not report it.
    | MissingAtSource of table: string
    /// The source reported a table the model does not hold. Not a fault in itself — the model
    /// need not name everything a source holds — but reported, so nothing is silent.
    | Unmodelled of table: string

/// Seeding a model from a reported schema, and holding a model against one.
module Drift =

    /// The id a seeded table carries: derived from its source and physical name, so seeding the
    /// same report twice mints the same ids.
    let tableId (source: string) (table: string) = "t:" + source + "." + table

    /// The id a seeded column carries.
    let columnId (source: string) (table: string) (column: string) =
        "c:" + source + "." + table + "." + column

    /// A model seeded from a reported schema: one source, a table per reported table, a column per
    /// reported column, each named as the source names it and classified `classification`, which
    /// is the model's one declared classification until an author declares more. Relations,
    /// measures, policies and limits are authored, never inferred.
    let seed
        (modelId: string)
        (modelName: string)
        (source: string)
        (classification: string)
        (reported: ReportedSchema)
        : ModelNode =
        let tables =
            reported.Tables
            |> List.map (fun t ->
                { Id = tableId source t.Name
                  Kind = NodeKind.Table { Name = t.Name; Physical = t.Name }
                  Children =
                    t.Columns
                    |> List.map (fun (c, ty) ->
                        { Id = columnId source t.Name c
                          Kind =
                            NodeKind.Column
                                { Name = c
                                  Physical = c
                                  Type = ty
                                  Classification = classification }
                          Children = [] }) })

        { Id = modelId
          Kind =
            NodeKind.Model
                { Name = modelName
                  Classifications = [ classification ] }
          Children =
            [ { Id = "s:" + source
                Kind = NodeKind.Source { Name = source }
                Children = tables } ] }

    /// Hold one source of `model` against what that source reported. Per table, the verdict is
    /// `Schema.diff`'s over physical names; model tables first, in model order, then the reported
    /// tables the model does not hold. `None` when the model has no source named `source`.
    let check (model: ModelNode) (source: string) (reported: ReportedSchema) : TableDrift list option =
        model.Children
        |> List.tryFind (fun n ->
            match n.Kind with
            | NodeKind.Source s -> s.Name = source
            | NodeKind.Model _
            | NodeKind.Table _
            | NodeKind.Column _
            | NodeKind.Join _
            | NodeKind.Measure _
            | NodeKind.RowPolicy _
            | NodeKind.Limit _ -> false)
        |> Option.map (fun src ->
            let modelled =
                src.Children
                |> List.choose (fun t ->
                    match t.Kind with
                    | NodeKind.Table spec -> Some(t, spec)
                    | NodeKind.Model _
                    | NodeKind.Source _
                    | NodeKind.Column _
                    | NodeKind.Join _
                    | NodeKind.Measure _
                    | NodeKind.RowPolicy _
                    | NodeKind.Limit _ -> None)

            let reportedBy = reported.Tables |> List.map (fun t -> t.Name, t) |> Map.ofList

            let verdicts =
                modelled
                |> List.map (fun (t, spec) ->
                    match Map.tryFind spec.Physical reportedBy with
                    | None -> TableDrift.MissingAtSource spec.Physical
                    | Some r ->
                        let delta = Schema.diff (Model.physicalSchemaOf t) r.Columns

                        if delta = Schema.identityDelta then
                            TableDrift.InStep spec.Physical
                        else
                            TableDrift.Drifted(spec.Physical, delta))

            let physical = modelled |> List.map (fun (_, s) -> s.Physical) |> Set.ofList

            let unmodelled =
                reported.Tables
                |> List.filter (fun t -> not (physical.Contains t.Name))
                |> List.map (fun t -> TableDrift.Unmodelled t.Name)

            verdicts @ unmodelled)

    /// Whether a verdict list holds anything but agreement and unmodelled tables.
    let hasDrift (verdicts: TableDrift list) : bool =
        verdicts
        |> List.exists (fun v ->
            match v with
            | TableDrift.Drifted _
            | TableDrift.MissingAtSource _ -> true
            | TableDrift.InStep _
            | TableDrift.Unmodelled _ -> false)
