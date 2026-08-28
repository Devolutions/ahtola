using System.Collections;
using System.Data.Common;

namespace Ahtola;

public class AhtolaParameterCollection : DbParameterCollection
{
    private readonly List<AhtolaParameter> _parameters = new();
    public override int Count => _parameters.Count;

    public override object SyncRoot => ((ICollection)_parameters).SyncRoot;

    public override int Add(object value)
    {
        _parameters.Add(value as AhtolaParameter ?? new AhtolaParameter(value));
        return _parameters.Count - 1;
    }

    public AhtolaParameter AddWithValue(string parameterName, object value)
    {
        var parameter = new AhtolaParameter(parameterName, value);
        _parameters.Add(parameter);
        return parameter;
    }

    public override void AddRange(Array values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var value = values.GetValue(i)!;
            var parameter = value as AhtolaParameter ?? new AhtolaParameter(value);
            _parameters.Add(parameter);
        }
    }

    public override void Clear()
    {
        _parameters.Clear();
    }

    public override bool Contains(object value)
    {
        return value is AhtolaParameter parameter
            ? _parameters.Contains(parameter)
            : _parameters.Any(p => Equals(p.Value, value));
    }

    public override bool Contains(string value)
    {
        return _parameters.Any(p => p.ParameterName == value);
    }

    public override void CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (var i = 0; i < _parameters.Count; i++)
            array.SetValue(_parameters[i], index + i);
    }

    public override IEnumerator GetEnumerator()
    {
        return _parameters.GetEnumerator();
    }

    public override int IndexOf(object value)
    {
        return value is AhtolaParameter parameter
            ? _parameters.IndexOf(parameter)
            : _parameters.FindIndex(p => Equals(p.Value, value));
    }

    public override int IndexOf(string parameterName)
    {
        return _parameters.FindIndex(p => p.ParameterName == parameterName);
    }

    public override void Insert(int index, object value)
    {
        _parameters.Insert(index, value as AhtolaParameter ?? new AhtolaParameter(value));
    }

    public override void Remove(object value)
    {
        var index = IndexOf(value);
        if (index == -1)
            throw new ArgumentException($"Parameter {value} not found");
        _parameters.RemoveAt(index);
    }

    public override void RemoveAt(int index)
    {
        _parameters.RemoveAt(index);
    }

    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index == -1)
            throw new ArgumentException($"Parameter {parameterName} not found");

        _parameters.RemoveAt(index);
    }

    protected override DbParameter GetParameter(int index)
    {
        return _parameters[index];
    }

    protected override DbParameter GetParameter(string parameterName)
    {
        return _parameters.Find(p => p.ParameterName == parameterName)
               ?? throw new ArgumentException($"Parameter {parameterName} not found");
    }

    protected override void SetParameter(int index, DbParameter value)
    {
        _parameters[index] = value as AhtolaParameter
                             ?? throw new ArgumentException($"Parameter {value} is not a AhtolaParameter");
    }

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index == -1)
            throw new ArgumentException($"Parameter {parameterName} not found");
        _parameters[index] = value as AhtolaParameter
                             ?? throw new ArgumentException($"Parameter {value} is not a AhtolaParameter");
    }
}
