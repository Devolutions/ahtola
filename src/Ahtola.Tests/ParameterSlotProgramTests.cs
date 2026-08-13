using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

// Program-level coverage for the late-bound parameter opcode: the ParameterSlot operand, the
// LoadParameterInstruction, VdbeProgram.ParameterSlotCount, the program validator's slot-range check, and
// how the instruction renders under EXPLAIN. These tests exercise the compiled-program contract directly,
// without any binding or execution, so they pin down the static shape the interpreter and builders rely on.
public class ParameterSlotProgramTests
{
    [Test]
    public void ParameterSlotRejectsNegativeIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParameterSlot(-1));
    }

    [Test]
    public void ParameterSlotIsValueEqualByIndex()
    {
        new ParameterSlot(3).Should().Be(new ParameterSlot(3));
        new ParameterSlot(3).Should().NotBe(new ParameterSlot(4));
        new ParameterSlot(3).Index.Should().Be(3);
    }

    [Test]
    public void ProgramExposesDeclaredParameterSlotCount()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadParameterInstruction(new Register(0), new ParameterSlot(1)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ],
            parameterSlotCount: 2);

        program.ParameterSlotCount.Should().Be(2);
    }

    [Test]
    public void ProgramWithoutParametersDeclaresZeroSlots()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadConstantInstruction(new Register(0), SqlValue.Integer(1)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]);

        program.ParameterSlotCount.Should().Be(0);
    }

    [Test]
    public void ConstructorRejectsNegativeParameterSlotCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new HaltInstruction()],
            parameterSlotCount: -1));
    }

    [Test]
    public void ValidatesLoadParameterWithinDeclaredSlotRange()
    {
        // Slots 0 and 1 are both inside a two-slot program, and the same slot may be read by more than one
        // instruction (the validator only checks the range, never uniqueness).
        var program = new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new LoadParameterInstruction(new Register(0), new ParameterSlot(0)),
                new LoadParameterInstruction(new Register(1), new ParameterSlot(1)),
                new LoadParameterInstruction(new Register(0), new ParameterSlot(0)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 2)),
                new HaltInstruction(),
            ],
            parameterSlotCount: 2);

        program.Validate();
        program.ParameterSlotCount.Should().Be(2);
    }

    [Test]
    public void RejectsLoadParameterReferencingSlotOutsideDeclaredRange()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadParameterInstruction(new Register(0), new ParameterSlot(2)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ],
            parameterSlotCount: 2));
    }

    [Test]
    public void RejectsLoadParameterWhenProgramDeclaresNoSlots()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadParameterInstruction(new Register(0), new ParameterSlot(0)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ]));
    }

    [Test]
    public void RejectsLoadParameterWithDestinationRegisterOutOfRange()
    {
        Assert.Throws<VdbeProgramValidationException>(() => new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadParameterInstruction(new Register(1), new ParameterSlot(0)),
                new HaltInstruction(),
            ],
            parameterSlotCount: 1));
    }

    [Test]
    public void LoadParameterOpcodeIsReported()
    {
        var instruction = new LoadParameterInstruction(new Register(0), new ParameterSlot(0));
        instruction.Opcode.Should().Be(VdbeOpcode.LoadParameter);
    }

    [Test]
    public void ExplainRendersLoadParameterAsSlotReference()
    {
        var (p1, p2, p3, p4, comment) =
            VdbeExplain.Describe(new LoadParameterInstruction(new Register(2), new ParameterSlot(3)));

        p1.Should().Be(2);
        p2.Should().Be(3);
        p3.Should().Be(0);
        p4.Should().Be("param[3]");
        comment.Should().Be("r[2]=param[3]");
    }

    [Test]
    public void ExplainDescribesLoadParameterRowWithinAProgram()
    {
        var program = new VdbeProgram(
            registerCount: 1,
            cursorCount: 0,
            [
                new LoadParameterInstruction(new Register(0), new ParameterSlot(0)),
                new ResultRowInstruction(new RegisterRange(new Register(0), 1)),
                new HaltInstruction(),
            ],
            parameterSlotCount: 1);

        var rows = VdbeExplain.Describe(program);

        rows[0][1].Should().Be(SqlValue.Text("LoadParameter"));
        rows[0][6].Should().Be(SqlValue.Text("r[0]=param[0]"));
    }
}
