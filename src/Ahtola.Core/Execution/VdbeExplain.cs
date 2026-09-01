using System.Globalization;
using System.Text;

namespace Ahtola.Core.Execution;

/// <summary>
/// Renders <see cref="VdbeInstruction"/>s into the <c>addr/opcode/p1/p2/p3/p4/comment</c>
/// shape that <c>EXPLAIN</c> reports. It mirrors the descriptions the wired database
/// emits for the shared opcodes and extends them to the sorter opcode family, so a
/// program that materializes and orders rows can be described end to end while its
/// wiring into the statement pipeline is completed by the database layer.
/// </summary>
public static class VdbeExplain
{
    /// <summary>The column names an <c>EXPLAIN</c> result set exposes.</summary>
    public static string[] Columns() => ["addr", "opcode", "p1", "p2", "p3", "p4", "comment"];

    /// <summary>Describes a whole program as one <c>EXPLAIN</c> row per instruction, with
    /// the address counting up from zero.</summary>
    public static IReadOnlyList<SqlValue[]> Describe(VdbeProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var rows = new List<SqlValue[]>(program.Instructions.Count);
        for (var address = 0; address < program.Instructions.Count; address++)
        {
            var instruction = program.Instructions[address];
            var (p1, p2, p3, p4, comment) = Describe(instruction);
            rows.Add(
            [
                SqlValue.Integer(address),
                SqlValue.Text(instruction.Opcode.ToString()),
                SqlValue.Integer(p1),
                SqlValue.Integer(p2),
                SqlValue.Integer(p3),
                p4 is null ? SqlValue.Null : SqlValue.Text(p4),
                SqlValue.Text(comment),
            ]);
        }

        return rows;
    }

    /// <summary>Describes a single instruction as its <c>(p1, p2, p3, p4, comment)</c> tuple.</summary>
    public static (long P1, long P2, long P3, string? P4, string Comment) Describe(VdbeInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        return instruction switch
        {
            LoadConstantInstruction load => (
                load.Destination.Index,
                0,
                0,
                FormatValue(load.Value),
                $"r[{load.Destination.Index}]={FormatValue(load.Value)}"),
            LoadParameterInstruction loadParameter => (
                loadParameter.Destination.Index,
                loadParameter.Slot.Index,
                0,
                $"param[{loadParameter.Slot.Index}]",
                $"r[{loadParameter.Destination.Index}]=param[{loadParameter.Slot.Index}]"),
            CopyInstruction copy => (
                copy.Source.Index,
                copy.Destination.Index,
                0,
                null,
                $"r[{copy.Destination.Index}]=r[{copy.Source.Index}]"),
            FunctionInstruction function => (
                function.Destination.Index,
                function.Arguments.Start.Index,
                function.Arguments.Count,
                function.Function.Name,
                $"r[{function.Destination.Index}]={function.Function.Name}({FormatRange(function.Arguments)})"),
            ArithmeticInstruction arithmetic => (
                arithmetic.Destination.Index,
                arithmetic.Operands.Start.Index,
                arithmetic.Operands.Count,
                VdbeArithmetic.Symbol(arithmetic.Operator),
                FormatArithmetic(arithmetic)),
            NumericAffinityInstruction numericAffinity => (
                numericAffinity.Value.Index,
                0,
                0,
                numericAffinity.Affinity.Name,
                $"r[{numericAffinity.Value.Index}]={numericAffinity.Affinity.Name}(r[{numericAffinity.Value.Index}])"),
            CompareInstruction compare => (
                compare.Destination.Index,
                compare.Left.Index,
                compare.Right.Index,
                compare.Operator.ToString(),
                $"r[{compare.Destination.Index}]=r[{compare.Left.Index}] {FormatComparison(compare.Operator)} r[{compare.Right.Index}]"),
            JumpIfNotTrueInstruction jumpIfNotTrue => (
                jumpIfNotTrue.Value.Index,
                jumpIfNotTrue.FalseTarget.Offset,
                0,
                null,
                $"goto {jumpIfNotTrue.FalseTarget.Offset} if r[{jumpIfNotTrue.Value.Index}] is not true"),
            CastInstruction cast => (
                cast.Value.Index,
                0,
                0,
                cast.TypeName,
                $"r[{cast.Value.Index}]=CAST(r[{cast.Value.Index}] AS {cast.TypeName})"),
            OpenReadCursorInstruction open => (
                open.Cursor.Index,
                0,
                open.ColumnCount,
                open.TableName,
                open.TableName is null
                    ? $"open read cursor {open.Cursor.Index}"
                    : $"open read cursor {open.Cursor.Index} on {open.TableName} ({open.ColumnCount} cols)"),
            OpenJoinCursorInstruction openJoin => (
                openJoin.Cursor.Index,
                openJoin.Plan.SourceCount,
                openJoin.Plan.RecordColumnCount,
                openJoin.Plan.Description,
                $"materialize {openJoin.Plan.Description} into cursor {openJoin.Cursor.Index} ({openJoin.Plan.RecordColumnCount} cols)"),
            OpenWriteCursorInstruction openWrite => (
                openWrite.Cursor.Index,
                0,
                openWrite.ColumnCount,
                openWrite.TableName,
                $"open write cursor {openWrite.Cursor.Index} on {openWrite.TableName} ({openWrite.ColumnCount} cols)"),
            VOpenInstruction vOpen => (
                vOpen.Cursor.Index,
                0,
                0,
                null,
                $"open virtual cursor {vOpen.Cursor.Index}"),
            IndexMethodCreateInstruction methodCreate => (
                methodCreate.Cursor.Index,
                0,
                0,
                methodCreate.Binding.IndexName,
                $"create {methodCreate.Binding.MethodName} index {methodCreate.Binding.IndexName} on cursor {methodCreate.Cursor.Index}"),
            IndexMethodDestroyInstruction methodDestroy => (
                methodDestroy.Cursor.Index,
                0,
                0,
                methodDestroy.Binding.IndexName,
                $"destroy {methodDestroy.Binding.MethodName} index {methodDestroy.Binding.IndexName} on cursor {methodDestroy.Cursor.Index}"),
            IndexMethodOptimizeInstruction methodOptimize => (
                methodOptimize.Cursor.Index,
                0,
                0,
                methodOptimize.Binding.IndexName,
                $"optimize {methodOptimize.Binding.MethodName} index {methodOptimize.Binding.IndexName} on cursor {methodOptimize.Cursor.Index}"),
            IndexMethodQueryInstruction methodQuery => (
                methodQuery.Cursor.Index,
                methodQuery.EmptyTarget.Offset,
                methodQuery.Arguments.Count,
                methodQuery.Binding.IndexName,
                $"query {methodQuery.Binding.MethodName} idx={methodQuery.Binding.IndexName} pattern={methodQuery.PatternIndex} args={FormatRange(methodQuery.Arguments)}"),
            IndexMethodNextInstruction methodNext => (
                methodNext.Cursor.Index,
                methodNext.LoopTarget.Offset,
                0,
                null,
                $"advance index-method cursor {methodNext.Cursor.Index} to {methodNext.LoopTarget.Offset}"),
            IndexMethodColumnInstruction methodColumn => (
                methodColumn.Cursor.Index,
                methodColumn.ColumnIndex,
                methodColumn.Destination.Index,
                null,
                $"read index-method column {methodColumn.ColumnIndex} from cursor {methodColumn.Cursor.Index} into r{methodColumn.Destination.Index}"),
            IndexMethodRowIdInstruction methodRowId => (
                methodRowId.Cursor.Index,
                0,
                methodRowId.Destination.Index,
                null,
                $"read index-method rowid from cursor {methodRowId.Cursor.Index} into r{methodRowId.Destination.Index}"),
            IndexMethodInsertInstruction methodInsert => (
                methodInsert.Cursor.Index,
                0,
                methodInsert.Values.Count,
                null,
                $"index-method insert {FormatRange(methodInsert.Values)} into cursor {methodInsert.Cursor.Index}"),
            IndexMethodDeleteInstruction methodDelete => (
                methodDelete.Cursor.Index,
                0,
                methodDelete.Values.Count,
                null,
                $"index-method delete {FormatRange(methodDelete.Values)} from cursor {methodDelete.Cursor.Index}"),
            VFilterInstruction vFilter => (
                vFilter.Cursor.Index,
                vFilter.EmptyTarget.Offset,
                vFilter.Arguments.Count,
                vFilter.Plan.IndexString,
                $"filter virtual cursor {vFilter.Cursor.Index} idx={vFilter.Plan.IndexNumber} args={FormatRange(vFilter.Arguments)}"),
            VColumnInstruction vColumn => (
                vColumn.Cursor.Index,
                vColumn.ColumnIndex,
                vColumn.Destination.Index,
                null,
                $"r[{vColumn.Destination.Index}]=v{vColumn.Cursor.Index}.col[{vColumn.ColumnIndex}]"),
            VUpdateInstruction vUpdate => (
                vUpdate.Cursor.Index,
                vUpdate.Arguments.Start.Index,
                vUpdate.Arguments.Count,
                vUpdate.ConflictMode.ToString().ToUpperInvariant(),
                $"update virtual cursor {vUpdate.Cursor.Index} from {FormatRange(vUpdate.Arguments)} conflict={vUpdate.ConflictMode}"),
            VNextInstruction vNext => (
                vNext.Cursor.Index,
                vNext.LoopTarget.Offset,
                0,
                null,
                $"virtual cursor {vNext.Cursor.Index} next, goto {vNext.LoopTarget.Offset}"),
            VCreateInstruction vCreate => (
                0,
                0,
                vCreate.Context.Arguments.Count,
                vCreate.ModuleName,
                $"create virtual table {vCreate.Context.TableName} using {vCreate.ModuleName}"),
            VDestroyInstruction vDestroy => (
                vDestroy.Cursor.Index,
                0,
                0,
                vDestroy.TableName,
                $"destroy virtual table {vDestroy.TableName}"),
            VRenameInstruction vRename => (
                vRename.Cursor.Index,
                vRename.NewName.Index,
                0,
                null,
                $"rename virtual cursor {vRename.Cursor.Index} using r[{vRename.NewName.Index}]"),
            MakeRecordInstruction makeRecord => (
                makeRecord.Values.Start.Index,
                makeRecord.Values.Count,
                makeRecord.Destination.Index,
                makeRecord.IndexName,
                $"r[{makeRecord.Destination.Index}]=mkrec(r[{makeRecord.Values.Start.Index}..{makeRecord.Values.Start.Index + makeRecord.Values.Count - 1}])"
                    + (makeRecord.IndexName is null ? string.Empty : $"; for {makeRecord.IndexName}")),
            NewRowidInstruction newRowid => (
                newRowid.Cursor.Index,
                newRowid.Destination.Index,
                newRowid.PreviousLargest?.Index ?? 0,
                null,
                $"r[{newRowid.Destination.Index}]=rowid"),
            CreateBtreeInstruction createBtree => (
                createBtree.Database,
                createBtree.RootDestination.Index,
                (long)createBtree.Flags,
                null,
                $"r[{createBtree.RootDestination.Index}]=root iDb={createBtree.Database} flags={(int)createBtree.Flags}"),
            ClearBtreeInstruction clearBtree => (
                clearBtree.RootPage,
                clearBtree.Database,
                0,
                null,
                $"root={clearBtree.RootPage} iDb={clearBtree.Database}"),
            DestroyInstruction destroy => (
                destroy.RootRegister?.Index ?? destroy.RootPage,
                destroy.FormerRootDestination.Index,
                destroy.IsTemporary ? 1 : 0,
                null,
                (destroy.RootRegister is { } destroyRootRegister
                    ? $"root=r[{destroyRootRegister.Index}]"
                    : $"root={destroy.RootPage}")
                    + $" iDb={destroy.Database} former_root={destroy.FormerRootDestination.Index}"
                    + $" is_temp={(destroy.IsTemporary ? 1 : 0)}"),
            IndexBuildInstruction indexBuild => (
                indexBuild.Database,
                indexBuild.Unique ? 1 : 0,
                0,
                indexBuild.IndexName,
                $"refill index {indexBuild.IndexName} from {indexBuild.TableName}"
                    + (indexBuild.Unique ? "; unique" : string.Empty)),
            ReadCookieInstruction readCookie => (
                readCookie.Database,
                readCookie.Destination.Index,
                (long)readCookie.Cookie,
                null,
                $"r[{readCookie.Destination.Index}]=cookie[{(int)readCookie.Cookie}] iDb={readCookie.Database}"),
            // Turso reports the SetCookie flag word as P5. VdbeExplain exposes no P5 column, so it is
            // carried in the comment rather than dropped.
            SetCookieInstruction setCookie => (
                setCookie.Database,
                (long)setCookie.Cookie,
                setCookie.Value,
                null,
                $"cookie[{(int)setCookie.Cookie}]={setCookie.Value} iDb={setCookie.Database} p5={setCookie.P5}"),
            ParseSchemaInstruction parseSchema => (
                parseSchema.Database,
                0,
                0,
                parseSchema.WhereClause ?? "NULL",
                parseSchema.WhereClause ?? "NULL"),
            DropTableInstruction dropTable => (
                dropTable.Database,
                0,
                0,
                dropTable.TableName,
                $"DROP TABLE {dropTable.TableName}"),
            DropViewInstruction dropView => (
                dropView.Database,
                0,
                0,
                dropView.ViewName,
                $"DROP VIEW {dropView.ViewName}"),
            DropIndexInstruction dropIndex => (
                0,
                0,
                0,
                dropIndex.IndexName,
                $"DROP INDEX {dropIndex.IndexName}"),
            DropTriggerInstruction dropTrigger => (
                dropTrigger.Database,
                0,
                0,
                dropTrigger.TriggerName,
                $"DROP TRIGGER {dropTrigger.TriggerName}"),
            RenameTableInstruction renameTable => (
                0,
                0,
                0,
                null,
                $"rename_table({renameTable.From}, {renameTable.To})"),
            AddColumnInstruction addColumn => (
                0,
                0,
                0,
                null,
                $"add_column({addColumn.TableName}, {addColumn.ColumnDefinition})"),
            DropColumnInstruction dropColumn => (
                0,
                0,
                0,
                null,
                $"drop_column({dropColumn.TableName}, {dropColumn.ColumnIndex})"),
            AlterColumnInstruction alterColumn => (
                0,
                0,
                0,
                null,
                $"alter_column({alterColumn.TableName}, {alterColumn.ColumnIndex}, {alterColumn.ColumnDefinition}, {(alterColumn.Rename ? "true" : "false")})"),
            VBeginInstruction vBegin => (vBegin.Cursor.Index, 0, 0, null, $"begin virtual cursor {vBegin.Cursor.Index}"),
            VSyncInstruction vSync => (vSync.Cursor.Index, 0, 0, null, $"sync virtual cursor {vSync.Cursor.Index}"),
            VCommitInstruction vCommit => (vCommit.Cursor.Index, 0, 0, null, $"commit virtual cursor {vCommit.Cursor.Index}"),
            VRollbackInstruction vRollback => (vRollback.Cursor.Index, 0, 0, null, $"rollback virtual cursor {vRollback.Cursor.Index}"),
            CloseCursorInstruction close => (close.Cursor.Index, 0, 0, null, $"close cursor {close.Cursor.Index}"),
            RewindCursorInstruction rewind => (
                rewind.Cursor.Index,
                rewind.EmptyTarget.Offset,
                0,
                null,
                $"rewind cursor {rewind.Cursor.Index}, goto {rewind.EmptyTarget.Offset} if empty"),
            LastCursorInstruction last => (
                last.Cursor.Index,
                last.EmptyTarget.Offset,
                0,
                null,
                $"cursor {last.Cursor.Index} last, goto {last.EmptyTarget.Offset} if empty"),
            ColumnInstruction column => (
                column.Cursor.Index,
                column.ColumnIndex,
                column.Destination.Index,
                null,
                $"r[{column.Destination.Index}]=c{column.Cursor.Index}.col[{column.ColumnIndex}]"),
            RowIdInstruction rowId => (
                rowId.Cursor.Index,
                rowId.Destination.Index,
                0,
                null,
                $"r[{rowId.Destination.Index}]=c{rowId.Cursor.Index}.rowid"),
            RowCountInstruction rowCount => (
                rowCount.Cursor.Index,
                rowCount.Destination.Index,
                0,
                null,
                $"r[{rowCount.Destination.Index}]=c{rowCount.Cursor.Index}.rowcount"),
            FilterInstruction filter => (
                filter.Cursor.Index,
                filter.FalseTarget.Offset,
                0,
                null,
                filter.Description),
            FilterRowIdInstruction filterRowId => (
                filterRowId.Cursor.Index,
                filterRowId.FalseTarget.Offset,
                0,
                null,
                filterRowId.Description),
            SeekRowidInstruction seekRowid => (
                seekRowid.Cursor.Index,
                seekRowid.NotFoundTarget.Offset,
                seekRowid.RowIdRegister.Index,
                null,
                seekRowid.Description),
            NotExistsInstruction notExists => (
                notExists.Cursor.Index,
                notExists.JumpTarget.Offset,
                notExists.RowIdRegister.Index,
                null,
                notExists.Description),
            FoundInstruction found => (
                found.Cursor.Index,
                found.FoundTarget.Offset,
                found.RowIdRegister.Index,
                null,
                found.Description),
            OpenEphemeralInstruction openEphemeral => (
                openEphemeral.Cursor.Index,
                openEphemeral.ColumnCount,
                0,
                null,
                $"open ephemeral cursor {openEphemeral.Cursor.Index} cols={openEphemeral.ColumnCount}"),
            EphemeralInsertInstruction ephemeralInsert => (
                ephemeralInsert.Cursor.Index,
                ephemeralInsert.Values.Start.Index,
                ephemeralInsert.Values.Count,
                null,
                $"insert into ephemeral cursor {ephemeralInsert.Cursor.Index} from {FormatRange(ephemeralInsert.Values)}"),
            NoConflictInstruction noConflict => (
                noConflict.Cursor.Index,
                noConflict.NoConflictTarget.Offset,
                noConflict.Key.Start.Index,
                FormatRange(noConflict.Key),
                noConflict.Description),
            FkCounterInstruction fkCounter => (
                fkCounter.Deferred ? 1 : 0,
                fkCounter.Increment,
                0,
                null,
                fkCounter.Deferred
                    ? $"fk deferred counter += {fkCounter.Increment}"
                    : $"fk statement counter += {fkCounter.Increment}"),
            FkIfZeroInstruction fkIfZero => (
                fkIfZero.Deferred ? 1 : 0,
                fkIfZero.Target.Offset,
                0,
                null,
                fkIfZero.Deferred
                    ? $"if deferred fk counter == 0 goto {fkIfZero.Target.Offset}"
                    : $"if statement fk counter == 0 goto {fkIfZero.Target.Offset}"),
            FkCheckInstruction fkCheck => (
                fkCheck.Deferred ? 1 : 0,
                0,
                0,
                null,
                fkCheck.Deferred ? "check deferred fk counter" : "check statement fk counter"),
            SeekKeyInstruction seekKey => (
                seekKey.Cursor.Index,
                seekKey.NotFoundTarget.Offset,
                seekKey.Key.Start.Index,
                $"{seekKey.Operator}{(seekKey.EqOnly ? " eq_only" : string.Empty)} {FormatRange(seekKey.Key)}",
                seekKey.Description),
            IdxRowIdInstruction idxRowId => (
                idxRowId.Cursor.Index,
                idxRowId.Destination.Index,
                0,
                null,
                $"r[{idxRowId.Destination.Index}]=idx rowid c[{idxRowId.Cursor.Index}]"),
            RowDataInstruction rowData => (
                rowData.Cursor.Index,
                rowData.Destination.Start.Index,
                rowData.Destination.Count,
                null,
                $"rowdata c[{rowData.Cursor.Index}] -> {FormatRange(rowData.Destination)}"),
            IdxInsertInstruction idxInsert => (
                idxInsert.Cursor.Index,
                (long)idxInsert.Flags,
                idxInsert.Key.Count,
                FormatRange(idxInsert.Key),
                $"idx insert c[{idxInsert.Cursor.Index}] flags={idxInsert.Flags}"),
            IdxDeleteInstruction idxDelete => (
                idxDelete.Cursor.Index,
                0,
                idxDelete.Key?.Count ?? 0,
                idxDelete.Key is { } k ? FormatRange(k) : null,
                $"idx delete c[{idxDelete.Cursor.Index}]"),
            SeekRowidRangeInstruction seekRowidRange => (
                seekRowidRange.Cursor.Index,
                seekRowidRange.NotFoundTarget.Offset,
                seekRowidRange.StartRowIdRegister.Index,
                seekRowidRange.EndRowIdRegister is null
                    ? $"{seekRowidRange.StartOp}"
                    : $"{seekRowidRange.StartOp}..{seekRowidRange.EndOp}",
                seekRowidRange.Description),
            FilterRegistersInstruction filterRegisters => (
                filterRegisters.Row.Start.Index,
                filterRegisters.FalseTarget.Offset,
                filterRegisters.Row.Count,
                null,
                filterRegisters.Description),
            GroupKeyInstruction groupKey => (
                groupKey.Row.Start.Index,
                groupKey.Destination.Index,
                groupKey.KeyCount,
                $"group-set[{groupKey.GroupSetIndex}]",
                $"r[{groupKey.Destination.Index}]=group key {FormatRange(groupKey.Row)} in set {groupKey.GroupSetIndex}"
                    + (groupKey.KeyOutput is { } keyOutput ? $", key to {FormatRange(keyOutput)}" : string.Empty)),
            ProjectRegistersInstruction project => (
                project.Input.Start.Index,
                project.Output.Start.Index,
                project.Output.Count,
                null,
                project.Description),
            DistinctFilterInstruction distinctFilter => (
                distinctFilter.Values.Start.Index,
                distinctFilter.DuplicateTarget.Offset,
                distinctFilter.DistinctSetIndex,
                null,
                $"goto {distinctFilter.DuplicateTarget.Offset} if {FormatRange(distinctFilter.Values)} is in distinct set {distinctFilter.DistinctSetIndex}"),
            NextInstruction next => (
                next.Cursor.Index,
                next.LoopTarget.Offset,
                0,
                null,
                $"next cursor {next.Cursor.Index}, goto {next.LoopTarget.Offset} if more rows"),
            PrevInstruction prev => (
                prev.Cursor.Index,
                prev.LoopTarget.Offset,
                0,
                null,
                $"cursor {prev.Cursor.Index} prev, goto {prev.LoopTarget.Offset} if more rows"),
            OpenSorterInstruction openSorter => (
                openSorter.Sorter.Index,
                0,
                openSorter.ColumnCount,
                null,
                $"open sorter {openSorter.Sorter.Index} ({openSorter.ColumnCount} cols)"),
            SorterInsertInstruction sorterInsert => (
                sorterInsert.Sorter.Index,
                sorterInsert.Record.Start.Index,
                sorterInsert.Record.Count,
                null,
                $"sorter {sorterInsert.Sorter.Index} insert {FormatRange(sorterInsert.Record)}"),
            SorterSortInstruction sorterSort => (
                sorterSort.Sorter.Index,
                sorterSort.EmptyTarget.Offset,
                0,
                null,
                $"sort sorter {sorterSort.Sorter.Index}, goto {sorterSort.EmptyTarget.Offset} if empty"),
            SorterDataInstruction sorterData => (
                sorterData.Sorter.Index,
                sorterData.Destination.Start.Index,
                sorterData.Destination.Count,
                null,
                $"{FormatRange(sorterData.Destination)}=sorter {sorterData.Sorter.Index} data"),
            SorterNextInstruction sorterNext => (
                sorterNext.Sorter.Index,
                sorterNext.LoopTarget.Offset,
                0,
                null,
                $"next sorter {sorterNext.Sorter.Index}, goto {sorterNext.LoopTarget.Offset} if more rows"),
            CloseSorterInstruction closeSorter => (
                closeSorter.Sorter.Index,
                0,
                0,
                null,
                $"close sorter {closeSorter.Sorter.Index}"),
            GotoInstruction gotoInstruction => (
                0,
                gotoInstruction.Target.Offset,
                0,
                null,
                $"goto {gotoInstruction.Target.Offset}"),
            JumpIfInstruction jumpIf => (
                jumpIf.Register.Index,
                jumpIf.Target.Offset,
                0,
                null,
                $"goto {jumpIf.Target.Offset} if r[{jumpIf.Register.Index}]"),
            AggResetInstruction aggReset => (
                aggReset.Accumulator.Index,
                0,
                0,
                null,
                $"reset accumulator {aggReset.Accumulator.Index}"),
            AggStepInstruction aggStep => (
                aggStep.Accumulator.Index,
                aggStep.Arguments.Start.Index,
                aggStep.Arguments.Count,
                aggStep.Aggregate.Name,
                $"accumulator {aggStep.Accumulator.Index}={aggStep.Aggregate.Name} step {FormatRange(aggStep.Arguments)}"),
            AggFinalizeInstruction aggFinalize => (
                aggFinalize.Accumulator.Index,
                aggFinalize.Destination.Index,
                0,
                aggFinalize.Aggregate.Name,
                $"r[{aggFinalize.Destination.Index}]={aggFinalize.Aggregate.Name} finalize accumulator {aggFinalize.Accumulator.Index}"),
            SameGroupInstruction sameGroup => (
                sameGroup.CurrentKey.Start.Index,
                sameGroup.SameGroupTarget.Offset,
                sameGroup.SavedKey.Start.Index,
                null,
                $"goto {sameGroup.SameGroupTarget.Offset} if group {FormatRange(sameGroup.CurrentKey)}=={FormatRange(sameGroup.SavedKey)}"),
            DeleteInstruction delete => (
                delete.Cursor.Index,
                0,
                0,
                null,
                $"delete current row of cursor {delete.Cursor.Index}"),
            InsertInstruction insert => (
                insert.Cursor.Index,
                (long)insert.Flags,
                insert.Record?.Index ?? 0,
                insert.TableName ?? (insert.Flags == VdbeInsertFlags.None ? null : insert.Flags.ToString()),
                DescribeInsert(insert)),
            UpdateInstruction update => (
                update.Cursor.Index,
                (long)update.Flags,
                0,
                update.Flags == VdbeInsertFlags.None ? null : update.Flags.ToString(),
                update.Flags == VdbeInsertFlags.None
                    ? $"update current row of cursor {update.Cursor.Index}"
                    : $"update current row of cursor {update.Cursor.Index} flags={update.Flags}"),
            ProgramInstruction program => (
                program.ParameterRegisters.Count == 0 ? 0 : program.ParameterRegisters[0].Index,
                program.IgnoreJumpTarget?.Offset ?? 0,
                program.ParameterRegisters.Count,
                "subprogram",
                $"invoke subprogram with {FormatRegisters(program.ParameterRegisters)}"
                    + (program.IgnoreJumpTarget is { } ignoreJumpTarget
                        ? $"; RAISE(IGNORE) goto {ignoreJumpTarget.Offset}"
                        : string.Empty)),
            CommitInstruction commit => (
                commit.Cursor.Index,
                0,
                0,
                null,
                $"commit mutations of cursor {commit.Cursor.Index}"),
            ResultRowInstruction result => (
                result.Values.Start.Index,
                result.Values.Count,
                0,
                null,
                FormatResultRow(result.Values)),
            DistinctResultRowInstruction distinct => (
                distinct.Values.Start.Index,
                distinct.Values.Count,
                distinct.DistinctSetIndex,
                null,
                $"{FormatResultRow(distinct.Values)} if new to distinct set {distinct.DistinctSetIndex}"),
            DistinctGateInstruction distinctGate => (
                distinctGate.Values.Start.Index,
                distinctGate.DuplicateTarget.Offset,
                distinctGate.DistinctSetIndex,
                null,
                $"goto {distinctGate.DuplicateTarget.Offset} if {FormatRange(distinctGate.Values)} is in distinct set {distinctGate.DistinctSetIndex}"),
            RowSetInsertInstruction rowSetInsert => (
                rowSetInsert.Values.Start.Index,
                rowSetInsert.Values.Count,
                rowSetInsert.RowSetIndex,
                null,
                $"insert {FormatRange(rowSetInsert.Values)} into row set {rowSetInsert.RowSetIndex}"),
            RowSetRewindInstruction rowSetRewind => (
                rowSetRewind.RowSetIndex,
                rowSetRewind.EmptyTarget.Offset,
                rowSetRewind.Destination.Start.Index,
                FormatRange(rowSetRewind.Destination),
                $"{FormatRange(rowSetRewind.Destination)}=row set {rowSetRewind.RowSetIndex} first, goto {rowSetRewind.EmptyTarget.Offset} if empty"),
            RowSetNextInstruction rowSetNext => (
                rowSetNext.RowSetIndex,
                rowSetNext.LoopTarget.Offset,
                rowSetNext.Destination.Start.Index,
                FormatRange(rowSetNext.Destination),
                $"{FormatRange(rowSetNext.Destination)}=row set {rowSetNext.RowSetIndex} next, goto {rowSetNext.LoopTarget.Offset} if present"),
            RowSetTestInstruction rowSetTest => (
                rowSetTest.RowSetRegister.Index,
                rowSetTest.FoundTarget.Offset,
                rowSetTest.ValueRegister.Index,
                rowSetTest.Batch.ToString(CultureInfo.InvariantCulture),
                $"goto {rowSetTest.FoundTarget.Offset} if r[{rowSetTest.ValueRegister.Index}] is in integer row set r[{rowSetTest.RowSetRegister.Index}] from an earlier batch"),
            CompoundResultRowInstruction compound => (
                compound.Values.Start.Index,
                compound.Values.Count,
                compound.OutputSetIndex,
                FormatSetList(compound.MembershipSetIndices),
                $"{FormatResultRow(compound.Values)} if new to distinct set {compound.OutputSetIndex} and {FormatMembership(compound.Mode)} {FormatSetList(compound.MembershipSetIndices)}"),
            GuardedRowInstruction guarded => (
                guarded.Values.Start.Index,
                guarded.Values.Count,
                guarded.Destination is RowSetDestination destination ? destination.RowSetIndex : -1,
                FormatGuards(guarded.Guards),
                $"{FormatDestination(guarded.Destination, guarded.Values)} if {FormatGuards(guarded.Guards)}"),
            RowGateInstruction rowGate => (
                rowGate.Values.Start.Index,
                rowGate.RejectTarget.Offset,
                rowGate.Values.Count,
                FormatGuards(rowGate.Guards),
                $"goto {rowGate.RejectTarget.Offset} unless {FormatRange(rowGate.Values)} {FormatGuards(rowGate.Guards)}"),
            OffsetGateInstruction offsetGate => (
                offsetGate.Counter.Index,
                offsetGate.SkipTarget.Offset,
                0,
                null,
                $"goto {offsetGate.SkipTarget.Offset} and decrement r[{offsetGate.Counter.Index}] while r[{offsetGate.Counter.Index}]>0"),
            LimitGateInstruction limitGate => (
                limitGate.Counter.Index,
                limitGate.DoneTarget.Offset,
                0,
                null,
                $"goto {limitGate.DoneTarget.Offset} when r[{limitGate.Counter.Index}]<=0, else decrement r[{limitGate.Counter.Index}]"),
            BeginTransactionInstruction => (0, 0, 0, null, "begin transaction"),
            CommitTransactionInstruction => (0, 0, 0, null, "commit transaction"),
            RollbackTransactionInstruction => (0, 0, 0, null, "rollback transaction"),
            SavepointInstruction savepoint => (0, 0, 0, savepoint.Name, $"open savepoint {savepoint.Name}"),
            ReleaseSavepointInstruction release => (0, 0, 0, release.Name, $"release savepoint {release.Name}"),
            RollbackToSavepointInstruction rollbackTo => (0, 0, 0, rollbackTo.Name, $"rollback to savepoint {rollbackTo.Name}"),
            OpenWorkTableInstruction openWorkTable => (
                openWorkTable.WorkTable.Index,
                openWorkTable.MaxRows,
                openWorkTable.MaxDepth,
                FormatDedupMode(openWorkTable.Mode),
                $"open work table {openWorkTable.WorkTable.Index} ({openWorkTable.ColumnCount} cols, {FormatDedupMode(openWorkTable.Mode)}, <={openWorkTable.MaxRows} rows, depth<={openWorkTable.MaxDepth})"),
            SeedWorkTableInstruction seed => (
                seed.WorkTable.Index,
                seed.Row.Start.Index,
                seed.Row.Count,
                null,
                $"seed work table {seed.WorkTable.Index} with {FormatRange(seed.Row)}"),
            WorkTableStepInstruction step => (
                step.WorkTable.Index,
                step.DoneTarget.Offset,
                step.Destination.Start.Index,
                null,
                $"{FormatRange(step.Destination)}=work table {step.WorkTable.Index} next, goto {step.DoneTarget.Offset} if drained"),
            WorkTableExpandInstruction expand => (
                expand.WorkTable.Index,
                expand.Source.Start.Index,
                expand.Source.Count,
                null,
                $"expand work table {expand.WorkTable.Index} from {FormatRange(expand.Source)}"),
            WorkTableExpandGenerationInstruction expandGeneration => (
                expandGeneration.WorkTable.Index,
                expandGeneration.Source.Start.Index,
                expandGeneration.Source.Count,
                null,
                $"expand work table {expandGeneration.WorkTable.Index} generation from {FormatRange(expandGeneration.Source)}"),
            CloseWorkTableInstruction closeWorkTable => (
                closeWorkTable.WorkTable.Index,
                0,
                0,
                null,
                $"close work table {closeWorkTable.WorkTable.Index}"),
            OpenWindowBufferInstruction openWindowBuffer => (
                openWindowBuffer.Buffer.Index,
                openWindowBuffer.ColumnCount,
                openWindowBuffer.WindowCount,
                null,
                $"open window buffer {openWindowBuffer.Buffer.Index} ({openWindowBuffer.ColumnCount} cols, {openWindowBuffer.WindowCount} windows)"),
            WindowBufferInsertInstruction windowInsert => (
                windowInsert.Buffer.Index,
                windowInsert.Record.Start.Index,
                windowInsert.Record.Count,
                null,
                $"window buffer {windowInsert.Buffer.Index} insert {FormatRange(windowInsert.Record)}"),
            WindowBufferComputeInstruction windowCompute => (
                windowCompute.Buffer.Index,
                windowCompute.EmptyTarget.Offset,
                0,
                null,
                $"compute window buffer {windowCompute.Buffer.Index}, goto {windowCompute.EmptyTarget.Offset} if empty"),
            WindowBufferDataInstruction windowData => (
                windowData.Buffer.Index,
                windowData.Destination.Start.Index,
                windowData.Destination.Count,
                null,
                $"{FormatRange(windowData.Destination)}=window buffer {windowData.Buffer.Index} data"),
            WindowBufferNextInstruction windowNext => (
                windowNext.Buffer.Index,
                windowNext.LoopTarget.Offset,
                0,
                null,
                $"next window buffer {windowNext.Buffer.Index}, goto {windowNext.LoopTarget.Offset} if more rows"),
            CloseWindowBufferInstruction closeWindowBuffer => (
                closeWindowBuffer.Buffer.Index,
                0,
                0,
                null,
                $"close window buffer {closeWindowBuffer.Buffer.Index}"),
            YieldInstruction => (0, 0, 0, null, "yield"),
            HaltInstruction halt => (
                halt.ErrorCode,
                0,
                halt.DescriptionRegister?.Index ?? 0,
                halt.OnError?.ToString() ?? halt.Description,
                halt.ErrorCode == 0
                    ? "halt"
                    : $"halt error {halt.ErrorCode}" + (halt.Description is null ? string.Empty : $": {halt.Description}")),
            HaltIfNullInstruction haltIfNull => (
                haltIfNull.ErrorCode,
                0,
                haltIfNull.Target.Index,
                haltIfNull.Description,
                $"halt if r[{haltIfNull.Target.Index}] is null"),
            _ => throw new VdbeProgramValidationException(
                $"Cannot describe unsupported opcode {instruction.Opcode}."),
        };
    }

    private static string FormatComparison(VdbeComparisonOperator operation)
        => operation switch
        {
            VdbeComparisonOperator.Is => "IS",
            VdbeComparisonOperator.IsNot => "IS NOT",
            VdbeComparisonOperator.Equal => "=",
            VdbeComparisonOperator.NotEqual => "!=",
            VdbeComparisonOperator.LessThan => "<",
            VdbeComparisonOperator.LessThanOrEqual => "<=",
            VdbeComparisonOperator.GreaterThan => ">",
            VdbeComparisonOperator.GreaterThanOrEqual => ">=",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown comparison operator."),
        };

    /// <summary>
    /// Describes an <see cref="InsertInstruction"/> in whichever addressing mode it uses: the
    /// register-backed form names the record and rowid registers Turso carries as <c>record_reg</c> and
    /// <c>key_reg</c>, while the cursor-only DML form keeps its existing wording.
    /// </summary>
    private static string DescribeInsert(InsertInstruction insert)
    {
        var target = insert.TableName is null
            ? $"cursor {insert.Cursor.Index}"
            : $"cursor {insert.Cursor.Index} ({insert.TableName})";
        var flags = insert.Flags == VdbeInsertFlags.None ? string.Empty : $" flags={insert.Flags}";
        return insert.Record is { } record && insert.RowId is { } rowId
            ? $"insert r[{record.Index}] into {target} key=r[{rowId.Index}]{flags}"
            : $"insert row into {target}{flags}";
    }

    private static string FormatRange(RegisterRange range)
    {
        if (range.Count == 0)
            return "r[]";

        var start = range.Start.Index;
        return range.Count == 1
            ? $"r[{start}]"
            : $"r[{start}..{start + range.Count - 1}]";
    }

    private static string FormatRegisters(IReadOnlyList<Register> registers)
        => registers.Count switch
        {
            0 => "r[]",
            1 => $"r[{registers[0].Index}]",
            _ => $"r[{string.Join(", ", registers.Select(register => register.Index))}]",
        };

    private static string FormatArithmetic(ArithmeticInstruction arithmetic)
    {
        var symbol = VdbeArithmetic.Symbol(arithmetic.Operator);
        var destination = arithmetic.Destination.Index;
        var start = arithmetic.Operands.Start.Index;
        // Unary operators render as a prefix over their single operand; binary operators render infix.
        return arithmetic.Operands.Count == 1
            ? $"r[{destination}]={symbol}r[{start}]"
            : $"r[{destination}]=r[{start}] {symbol} r[{start + 1}]";
    }

    private static string FormatResultRow(RegisterRange range)
        => $"output={FormatRange(range)}";

    private static string FormatDestination(VdbeRowDestination destination, RegisterRange values)
        => destination switch
        {
            ResultRowDestination => FormatResultRow(values),
            RowSetDestination rowSet => $"insert {FormatRange(values)} into row set {rowSet.RowSetIndex}",
            _ => throw new VdbeProgramValidationException(
                $"Unknown guarded-row destination {destination.GetType().Name}."),
        };

    private static string FormatGuards(IReadOnlyList<VdbeRowGuard> guards)
    {
        if (guards.Count == 0)
            return "accepted";

        return string.Join(
            " then ",
            guards.Select(guard => guard switch
            {
                DistinctRowGuard distinct => $"new to distinct set {distinct.RowSetIndex}",
                MembershipRowGuard membership
                    => $"{FormatMembership(membership.Mode)} {FormatSetList(membership.RowSetIndices)}",
                _ => throw new VdbeProgramValidationException(
                    $"Unknown guarded-row condition {guard.GetType().Name}."),
            }));
    }

    private static string FormatMembership(CompoundMembershipMode mode) => mode switch
    {
        CompoundMembershipMode.PresentInAll => "present in all of",
        CompoundMembershipMode.AbsentFromAll => "absent from all of",
        _ => throw new VdbeProgramValidationException($"Unknown compound membership mode {mode}."),
    };

    private static string FormatDedupMode(WorkTableDedupMode mode) => mode switch
    {
        WorkTableDedupMode.KeepAll => "union all",
        WorkTableDedupMode.Distinct => "distinct",
        _ => throw new VdbeProgramValidationException($"Unknown work table dedup mode {mode}."),
    };

    private static string FormatSetList(IReadOnlyList<int> setIndices)
    {
        if (setIndices is null || setIndices.Count == 0)
            return "sets {}";

        return $"sets {{{string.Join(",", setIndices)}}}";
    }

    private static string FormatValue(SqlValue value)
    {
        return value.Kind switch
        {
            SqlValueKind.Null => "NULL",
            SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString("R", CultureInfo.InvariantCulture),
            SqlValueKind.Text => $"'{value.AsText()}'",
            SqlValueKind.Blob => FormatBlob(value.AsBlob().Span),
            _ => throw new VdbeProgramValidationException($"Unknown SQL value kind {value.Kind}."),
        };
    }

    private static string FormatBlob(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2 + 3);
        builder.Append("x'");
        foreach (var b in bytes)
            builder.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        builder.Append('\'');
        return builder.ToString();
    }
}
