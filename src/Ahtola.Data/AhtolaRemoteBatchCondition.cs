namespace Ahtola;

/// <summary>
/// A server-side condition controlling whether one remote batch step executes.
/// Step references use the zero-based index of an earlier batch command.
/// </summary>
public sealed class AhtolaRemoteBatchCondition
{
    private AhtolaRemoteBatchCondition(
        string type,
        int? step = null,
        AhtolaRemoteBatchCondition? operand = null,
        IReadOnlyList<AhtolaRemoteBatchCondition>? operands = null)
    {
        Type = type;
        Step = step;
        Operand = operand;
        Operands = operands;
    }

    /// <summary>Gets a condition that is true when the connection is in autocommit mode.</summary>
    public static AhtolaRemoteBatchCondition IsAutocommit { get; } = new("is_autocommit");

    internal string Type { get; }
    internal int? Step { get; }
    internal AhtolaRemoteBatchCondition? Operand { get; }
    internal IReadOnlyList<AhtolaRemoteBatchCondition>? Operands { get; }

    /// <summary>Creates a condition requiring an earlier step to have succeeded.</summary>
    public static AhtolaRemoteBatchCondition StepSucceeded(int step)
        => new("ok", ValidateStep(step));

    /// <summary>Creates a condition requiring an earlier step to have failed.</summary>
    public static AhtolaRemoteBatchCondition StepFailed(int step)
        => new("error", ValidateStep(step));

    /// <summary>Negates another batch condition.</summary>
    public static AhtolaRemoteBatchCondition Not(AhtolaRemoteBatchCondition condition)
        => new("not", operand: condition ?? throw new ArgumentNullException(nameof(condition)));

    /// <summary>Creates a condition requiring every supplied condition to be true.</summary>
    public static AhtolaRemoteBatchCondition And(params AhtolaRemoteBatchCondition[] conditions)
        => new("and", operands: ValidateOperands(conditions));

    /// <summary>Creates a condition requiring at least one supplied condition to be true.</summary>
    public static AhtolaRemoteBatchCondition Or(params AhtolaRemoteBatchCondition[] conditions)
        => new("or", operands: ValidateOperands(conditions));

    private static int ValidateStep(int step)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(step);
        return step;
    }

    private static AhtolaRemoteBatchCondition[] ValidateOperands(AhtolaRemoteBatchCondition[] conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        if (conditions.Length == 0)
            throw new ArgumentException("At least one batch condition is required.", nameof(conditions));
        if (conditions.Any(static condition => condition is null))
            throw new ArgumentException("Batch conditions cannot contain null values.", nameof(conditions));

        return conditions;
    }
}
