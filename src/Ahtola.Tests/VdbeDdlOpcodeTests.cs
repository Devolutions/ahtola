using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

public sealed class VdbeDdlOpcodeTests
{
    [Test]
    public void ExistingVirtualDdlOpcodesRetainTheirPublicConstructorsAndExplainSurface()
    {
        var create = new VCreateInstruction(
            "test_module",
            new ManagedVirtualTableCreateContext("entries", ["tokenize=unicode61"]),
            static _ => { });
        var destroy = new VDestroyInstruction(new Cursor(2), "entries");
        var rename = new VRenameInstruction(new Cursor(2), new Register(3));

        create.Opcode.Should().Be(VdbeOpcode.VCreate);
        destroy.Opcode.Should().Be(VdbeOpcode.VDestroy);
        rename.Opcode.Should().Be(VdbeOpcode.VRename);

        VdbeExplain.Describe(create).Should().Be(
            (0L, 0L, 1L, "test_module", "create virtual table entries using test_module"));
        VdbeExplain.Describe(destroy).Should().Be(
            (2L, 0L, 0L, "entries", "destroy virtual table entries"));
        VdbeExplain.Describe(rename).Should().Be(
            (2L, 3L, 0L, null, "rename virtual cursor 2 using r[3]"));
    }

    [Test]
    public void ExistingVirtualDdlOpcodesKeepTheirValuesBeneathTheAppendedDdlPrerequisites()
    {
        ((int)VdbeOpcode.IndexMethodDelete).Should().Be(115);
        ((int)VdbeOpcode.VCreate).Should().Be(116);
        ((int)VdbeOpcode.VDestroy).Should().Be(117);
        ((int)VdbeOpcode.VRename).Should().Be(118);
    }

    [Test]
    public void TheIndexDdlOpcodesAreAppendedAfterTheSchemaFamilyWithoutRenumberingIt()
    {
        ((int)VdbeOpcode.Destroy).Should().Be(123);
        ((int)VdbeOpcode.DropIndex).Should().Be(129);
        ((int)VdbeOpcode.AlterColumn).Should().Be(134);
        ((int)VdbeOpcode.IndexBuild).Should().Be(135);
    }

    [Test]
    public void DestroyKeepsItsLiteralRootConstructorAndGainsARegisterForm()
    {
        var literal = new DestroyInstruction(0, 7, new Register(1));
        var temporary = new DestroyInstruction(1, 9, new Register(2), IsTemporary: true);
        var register = new DestroyInstruction(0, 0, new Register(1), IsTemporary: false, new Register(4));

        literal.Opcode.Should().Be(VdbeOpcode.Destroy);
        VdbeExplain.Describe(literal).Should().Be(
            (7L, 1L, 0L, null, "root=7 iDb=0 former_root=1 is_temp=0"));
        VdbeExplain.Describe(temporary).Should().Be(
            (9L, 2L, 1L, null, "root=9 iDb=1 former_root=2 is_temp=1"));
        VdbeExplain.Describe(register).Should().Be(
            (4L, 1L, 0L, null, "root=r[4] iDb=0 former_root=1 is_temp=0"));
    }

    [Test]
    public void IndexBuildDescribesTheIndexItRefillsAndWhetherItIsUnique()
    {
        var ordinary = new IndexBuildInstruction(0, "t", "idx");
        var unique = new IndexBuildInstruction(1, "t", "idx", Unique: true);

        ordinary.Opcode.Should().Be(VdbeOpcode.IndexBuild);
        VdbeExplain.Describe(ordinary).Should().Be((0L, 0L, 0L, "idx", "refill index idx from t"));
        VdbeExplain.Describe(unique).Should().Be((1L, 1L, 0L, "idx", "refill index idx from t; unique"));
    }

    [Test]
    public void DestroyRejectsSupplyingBothALiteralRootAndARootRegister()
    {
        Action build = () => new VdbeProgram(
            registerCount: 2,
            cursorCount: 0,
            [
                new DestroyInstruction(0, 7, new Register(0), IsTemporary: false, new Register(1)),
                new HaltInstruction(),
            ]);

        build.Should().Throw<VdbeProgramValidationException>()
            .WithMessage("*supplies both a literal root page and a root register to Destroy*");
    }

    [Test]
    public void IndexBuildRejectsAnEmptyTableOrIndexName()
    {
        Action emptyIndex = () => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new IndexBuildInstruction(0, "t", "  "), new HaltInstruction()]);
        Action emptyTable = () => new VdbeProgram(
            registerCount: 0,
            cursorCount: 0,
            [new IndexBuildInstruction(0, "  ", "idx"), new HaltInstruction()]);

        emptyIndex.Should().Throw<VdbeProgramValidationException>();
        emptyTable.Should().Throw<VdbeProgramValidationException>();
    }
}
