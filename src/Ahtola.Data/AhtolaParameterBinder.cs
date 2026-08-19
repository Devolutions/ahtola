using Ahtola.Core.Parsing;

namespace Ahtola;

internal sealed class AhtolaParameterBindings
{
    private readonly AhtolaParameter?[] _parameters;

    private AhtolaParameterBindings(SqlParameterMap map, AhtolaParameter?[] parameters)
    {
        Map = map;
        _parameters = parameters;
    }

    public SqlParameterMap Map { get; }

    public AhtolaParameter GetParameter(int index)
        => _parameters[index]
           ?? throw new InvalidOperationException(GetMissingParameterMessage(Map, index));

    public static AhtolaParameterBindings Create(string sql, AhtolaParameterCollection parameters)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);

        var map = SqlParameterMap.Parse(sql);
        var bindings = new AhtolaParameter?[map.Count + 1];
        var positional = new List<AhtolaParameter>();

        foreach (AhtolaParameter parameter in parameters)
        {
            if (string.IsNullOrEmpty(parameter.ParameterName))
            {
                positional.Add(parameter);
                continue;
            }

            var index = FindNamedParameter(map, parameter.ParameterName);
            if (index != 0)
                bindings[index] = parameter;
        }

        var positionalIndex = 0;
        for (var index = 1; index <= map.Count; index++)
        {
            if (!map.IsReferenced(index) || bindings[index] is not null)
                continue;
            if (positionalIndex < positional.Count)
                bindings[index] = positional[positionalIndex++];
        }

        for (var index = 1; index <= map.Count; index++)
        {
            if (map.IsReferenced(index) && bindings[index] is null)
                throw new InvalidOperationException(GetMissingParameterMessage(map, index));
        }

        return new AhtolaParameterBindings(map, bindings);
    }

    private static int FindNamedParameter(SqlParameterMap map, string parameterName)
    {
        if (map.TryGetIndex(parameterName, out var exactIndex))
            return exactIndex;
        if (parameterName.Length > 1 && parameterName[0] == '?')
            return 0;

        var unprefixedName = IsNamedPrefix(parameterName[0])
            ? parameterName[1..]
            : parameterName;
        var aliasIndex = 0;
        for (var index = 1; index <= map.Count; index++)
        {
            var sqlName = map.GetName(index);
            if (sqlName is null
                || !IsNamedPrefix(sqlName[0])
                || !sqlName.AsSpan(1).SequenceEqual(unprefixedName.AsSpan()))
            {
                continue;
            }

            if (aliasIndex != 0)
                throw new InvalidOperationException($"Parameter name {parameterName} is ambiguous.");
            aliasIndex = index;
        }

        return aliasIndex;
    }

    private static bool IsNamedPrefix(char value) => value is '@' or '$' or ':';

    private static string GetMissingParameterMessage(SqlParameterMap map, int index)
    {
        var parameterName = map.GetName(index);
        return parameterName is null
            ? $"Missing value for parameter ?{index}."
            : $"Missing value for parameter {parameterName}.";
    }
}
