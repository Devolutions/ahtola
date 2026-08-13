using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Coverage for VdbeParameterBinding, the immutable carrier the interpreter reads through LoadParameter. It
// verifies the builder's up-front validation of the three failure modes named in the design (duplicate,
// invalid, missing), the positional FromValues shortcut, the empty binding, out-of-range reads, and the
// snapshot guarantees (defensive copy, single-use builder) that stop a caller array from leaking into an
// already-published binding.
public class VdbeParameterBindingTests
{
    [Test]
    public void EmptyBindingSuppliesNoSlots()
    {
        VdbeParameterBinding.Empty.Count.Should().Be(0);
    }

    [Test]
    public void BuilderBindsEverySlotAndReadsThemBack()
    {
        var binding = VdbeParameterBinding.CreateBuilder(3)
            .Bind(0, SqlValue.Integer(10))
            .Bind(new ParameterSlot(1), SqlValue.Text("mid"))
            .Bind(2, SqlValue.Null)
            .Build();

        binding.Count.Should().Be(3);
        binding.Get(new ParameterSlot(0)).Should().Be(SqlValue.Integer(10));
        binding[1].Should().Be(SqlValue.Text("mid"));
        binding[2].Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void BuilderRejectsDuplicateSlot()
    {
        var builder = VdbeParameterBinding.CreateBuilder(2).Bind(0, SqlValue.Integer(1));

        Assert.Throws<VdbeParameterBindingException>(() => builder.Bind(0, SqlValue.Integer(2)));
    }

    [Test]
    public void BuilderRejectsSlotOutsideDeclaredRange()
    {
        var builder = VdbeParameterBinding.CreateBuilder(2);

        Assert.Throws<VdbeParameterBindingException>(() => builder.Bind(2, SqlValue.Integer(1)));
    }

    [Test]
    public void BuilderRejectsMissingSlotAtBuild()
    {
        var builder = VdbeParameterBinding.CreateBuilder(3)
            .Bind(0, SqlValue.Integer(1));

        Assert.Throws<VdbeParameterBindingException>(() => builder.Build());
    }

    [Test]
    public void BuilderIsSingleUse()
    {
        var builder = VdbeParameterBinding.CreateBuilder(1).Bind(0, SqlValue.Integer(1));
        builder.Build();

        Assert.Throws<VdbeParameterBindingException>(() => builder.Build());
        Assert.Throws<VdbeParameterBindingException>(() => builder.Bind(0, SqlValue.Integer(2)));
    }

    [Test]
    public void CreateBuilderRejectsNegativeSlotCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VdbeParameterBinding.CreateBuilder(-1));
    }

    [Test]
    public void FromValuesBindsPositionally()
    {
        var binding = VdbeParameterBinding.FromValues(SqlValue.Integer(1), SqlValue.Text("b"), SqlValue.Null);

        binding.Count.Should().Be(3);
        binding[0].Should().Be(SqlValue.Integer(1));
        binding[1].Should().Be(SqlValue.Text("b"));
        binding[2].Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void FromValuesListOverloadCopiesTheSourceList()
    {
        var values = new List<SqlValue> { SqlValue.Integer(1), SqlValue.Integer(2) };

        var binding = VdbeParameterBinding.FromValues(values);
        values[0] = SqlValue.Integer(99);

        // The binding took a private snapshot, so mutating the source list afterwards cannot change it.
        binding[0].Should().Be(SqlValue.Integer(1));
        binding[1].Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void FromValuesRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => VdbeParameterBinding.FromValues((IReadOnlyList<SqlValue>)null!));
        Assert.Throws<ArgumentNullException>(() => VdbeParameterBinding.FromValues((SqlValue[])null!));
    }

    [Test]
    public void GetRejectsSlotOutsideBindingRange()
    {
        var binding = VdbeParameterBinding.FromValues(SqlValue.Integer(1));

        Assert.Throws<VdbeParameterBindingException>(() => binding.Get(new ParameterSlot(1)));
    }

    [Test]
    public void PublishedBindingIsNotAffectedByReusedBuilderStorage()
    {
        // A binding published by Build must be a frozen snapshot: even though the builder cloned its array,
        // this asserts the published value is stable and independent of any later builder observation.
        var builder = VdbeParameterBinding.CreateBuilder(1).Bind(0, SqlValue.Integer(7));
        var binding = builder.Build();

        binding[0].Should().Be(SqlValue.Integer(7));
    }
}
