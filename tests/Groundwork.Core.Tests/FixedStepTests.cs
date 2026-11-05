namespace Groundwork.Core.Tests;

[TestClass]
public sealed class FixedStepTests
{
    [TestMethod]
    public void PositiveStepPreservesItsRationalValues()
    {
        var step = new FixedStep(1, 60);
        var (numerator, denominator) = step;

        Assert.AreEqual(1, numerator);
        Assert.AreEqual(60, denominator);
        Assert.AreEqual(new FixedStep(1, 30), step with { Denominator = 30 });
    }

    [TestMethod]
    [DataRow(0, 60)]
    [DataRow(-1, 60)]
    [DataRow(1, 0)]
    [DataRow(1, -60)]
    public void NonPositiveStepIsRejected(int numerator, int denominator) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new FixedStep(numerator, denominator));

    [TestMethod]
    public void UpdatingStepCannotIntroduceAnInvalidValue()
    {
        var step = new FixedStep(1, 60);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => step with { Numerator = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => step with { Denominator = 0 });
    }
}
