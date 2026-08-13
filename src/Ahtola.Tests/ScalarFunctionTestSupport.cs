using System.Text;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Shared scalar-function delegates for the scalar-function opcode, program-builder, and EXPLAIN tests.
// The delegates model the exact per-function contracts the executor relies on the caller to supply
// (NULL propagation, text/blob rules, error raising), so the tests exercise real function evaluation
// through the FunctionInstruction opcode rather than stubs.
internal static class ScalarFunctionTestSupport
{
    // abs(x): integer absolute value; NULL propagates to NULL. Arity 1.
    public static VdbeScalarFunction Abs() => new()
    {
        Name = "abs",
        Arity = 1,
        Invoke = arguments =>
        {
            var value = arguments[0];
            return value.Kind == SqlValueKind.Integer
                ? SqlValue.Integer(Math.Abs(value.AsInteger()))
                : SqlValue.Null;
        },
    };

    // add(a, b): integer addition; a NULL operand propagates to NULL. Arity 2.
    public static VdbeScalarFunction Add() => new()
    {
        Name = "add",
        Arity = 2,
        Invoke = arguments =>
        {
            if (arguments[0].Kind != SqlValueKind.Integer || arguments[1].Kind != SqlValueKind.Integer)
                return SqlValue.Null;

            return SqlValue.Integer(arguments[0].AsInteger() + arguments[1].AsInteger());
        },
    };

    // upper(x): ASCII upper-casing of text; NULL propagates to NULL. Arity 1.
    public static VdbeScalarFunction Upper() => new()
    {
        Name = "upper",
        Arity = 1,
        Invoke = arguments =>
        {
            var value = arguments[0];
            return value.Kind == SqlValueKind.Text
                ? SqlValue.Text(value.AsText().ToUpperInvariant())
                : SqlValue.Null;
        },
    };

    // coalesce(...): first non-NULL argument, or NULL when every argument is NULL. Variadic (no arity).
    public static VdbeScalarFunction Coalesce() => new()
    {
        Name = "coalesce",
        Arity = null,
        Invoke = arguments =>
        {
            foreach (var argument in arguments)
            {
                if (argument.Kind != SqlValueKind.Null)
                    return argument;
            }

            return SqlValue.Null;
        },
    };

    // reverse_blob(x): returns a fresh blob with the argument blob's bytes reversed; NULL propagates.
    // Exercises BLOB in and BLOB out through the copy-safe argument/result path. Arity 1.
    public static VdbeScalarFunction ReverseBlob() => new()
    {
        Name = "reverse_blob",
        Arity = 1,
        Invoke = arguments =>
        {
            var value = arguments[0];
            if (value.Kind != SqlValueKind.Blob)
                return SqlValue.Null;

            var bytes = value.AsBlob().ToArray();
            Array.Reverse(bytes);
            return SqlValue.Blob(bytes);
        },
    };

    // blob_len(x): the length of a blob argument as an integer; NULL propagates. Arity 1.
    public static VdbeScalarFunction BlobLength() => new()
    {
        Name = "blob_len",
        Arity = 1,
        Invoke = arguments =>
        {
            var value = arguments[0];
            return value.Kind == SqlValueKind.Blob
                ? SqlValue.Integer(value.AsBlob().Length)
                : SqlValue.Null;
        },
    };

    // concat(...): concatenates the text form of every argument (NULL contributes nothing). Variadic.
    public static VdbeScalarFunction Concat() => new()
    {
        Name = "concat",
        Arity = null,
        Invoke = arguments =>
        {
            var builder = new StringBuilder();
            foreach (var argument in arguments)
            {
                if (argument.Kind == SqlValueKind.Text)
                    builder.Append(argument.AsText());
                else if (argument.Kind == SqlValueKind.Integer)
                    builder.Append(argument.AsInteger());
            }

            return SqlValue.Text(builder.ToString());
        },
    };

    // always_42(): a nullary constant function, exercising the zero-argument path. Arity 0.
    public static VdbeScalarFunction Always42() => new()
    {
        Name = "always_42",
        Arity = 0,
        Invoke = _ => SqlValue.Integer(42),
    };

    // boom(x): always raises a function-level error, exercising error propagation out of a step. Arity 1.
    public static VdbeScalarFunction Boom() => new()
    {
        Name = "boom",
        Arity = 1,
        Invoke = _ => throw new VdbeFunctionException("boom() always fails."),
    };

    // scribble(x): mutates the argument tuple it is handed, then returns a constant. Proves the executor
    // passes a private copy of the registers: a well-behaved program's registers must be unaffected. Arity 1.
    public static VdbeScalarFunction Scribble() => new()
    {
        Name = "scribble",
        Arity = 1,
        Invoke = arguments =>
        {
            arguments[0] = SqlValue.Integer(999);
            return SqlValue.Integer(1);
        },
    };
}
