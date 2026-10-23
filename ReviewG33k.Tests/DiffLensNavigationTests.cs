using Avalonia;
using Avalonia.Input;
using ReviewG33k.Services.DiffLens;
using ReviewG33k.Views.DiffLens;

namespace ReviewG33k.Tests;

[TestFixture]
public sealed class DiffLensNavigationTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void MiddleClickFitsFileHeaderOrWholeCanvas(bool header)
    {
        var canvas = new DiffLensControl();
        canvas.Measure(new Size(1200, 800));
        canvas.Arrange(new Rect(0, 0, 1200, 800));
        var cells = new[]
        {
            new DiffLensCell(new DiffLensEntry("first.cpp", null, "preview")),
            new DiffLensCell(new DiffLensEntry("second.cpp", null, "preview"))
        };
        canvas.SetFiles("repo", cells);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var offset = (Vector)typeof(DiffLensControl).GetField("m_offset", flags)!.GetValue(canvas)!;
        var point = (cells[1].Position + new Vector(100, header ? 20 : 100)) * canvas.Zoom + offset;
        var pointer = new Pointer(1, PointerType.Mouse, true);
        canvas.RaiseEvent(new PointerPressedEventArgs(canvas, pointer, canvas, point, 0,
            new PointerPointProperties(Avalonia.Input.RawInputModifiers.MiddleMouseButton, PointerUpdateKind.MiddleButtonPressed), KeyModifiers.None));
        canvas.RaiseEvent(new PointerReleasedEventArgs(canvas, pointer, canvas, point, 0,
            new PointerPointProperties(), KeyModifiers.None, MouseButton.Middle));
        var target = (double)typeof(DiffLensControl).GetField("m_fitEndZoom", flags)!.GetValue(canvas)!;
        if (header)
        {
            Assert.That(canvas.SelectedPath, Is.EqualTo("second.cpp"));
            Assert.That(target, Is.EqualTo(Math.Min(1140 / cells[1].Bounds.Width, 740 / cells[1].Bounds.Height)).Within(0.001));
        }
        else
            Assert.That(target, Is.EqualTo(canvas.Zoom).Within(0.001));
        canvas.StopAnimations();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ClickingAddedLineTargetsItsWorkingLine(bool split)
    {
        var rows = DiffLensRows.Build("before\nafter", "before\ninserted\nafter", 0, split);
        var index = rows.ToList().FindIndex(row => row.Right?.Text == "inserted");
        Assert.That(DiffLensRows.FindWorkingLine(rows, index), Is.EqualTo(2));
    }

    [Test]
    public void ClickingDeletedLineTargetsNextSurvivingWorkingLine()
    {
        var rows = DiffLensRows.Build("before\ndeleted\nafter", "before\nafter", 4, false);
        var index = rows.ToList().FindIndex(row => row.Left?.Text == "deleted");
        Assert.That(DiffLensRows.FindWorkingLine(rows, index), Is.EqualTo(2));
    }

    [Test]
    public void ClickingDeletedEndTargetsPreviousSurvivingWorkingLine()
    {
        var rows = DiffLensRows.Build("before\ndeleted", "before", 4, false);
        var index = rows.ToList().FindIndex(row => row.Left?.Text == "deleted");
        Assert.That(DiffLensRows.FindWorkingLine(rows, index), Is.EqualTo(1));
    }

    [Test]
    public void TrackpadPinchAppliesIncrementalZoomAndConsumesGesture()
    {
        var canvas = new DiffLensControl();
        var initialZoom = canvas.Zoom;
        var pointer = new Pointer(1, PointerType.Mouse, true);
        foreach (var delta in new[] { 0.2, -0.1 })
        {
            var gesture = new PointerDeltaEventArgs(
                Gestures.PointerTouchPadGestureMagnifyEvent, canvas, pointer, canvas,
                new Point(100, 80), 0, new PointerPointProperties(), KeyModifiers.None,
                new Vector(delta, delta));

            canvas.RaiseEvent(gesture);

            Assert.That(gesture.Handled, Is.True);
        }

        Assert.That(canvas.Zoom, Is.EqualTo(initialZoom * 1.2 * 0.9).Within(0.000001));
    }

    [TestCase(100, 2.5)]
    [TestCase(-1, 0.001)]
    public void TrackpadPinchRespectsZoomLimits(double delta, double expectedZoom)
    {
        var canvas = new DiffLensControl();
        var gesture = new PointerDeltaEventArgs(
            Gestures.PointerTouchPadGestureMagnifyEvent, canvas,
            new Pointer(1, PointerType.Mouse, true), canvas, new Point(100, 80), 0,
            new PointerPointProperties(), KeyModifiers.None, new Vector(delta, delta));

        canvas.RaiseEvent(gesture);

        Assert.That(canvas.Zoom, Is.EqualTo(expectedZoom));
    }


    [TestCase(KeyModifiers.None)]
    [TestCase(KeyModifiers.Control)]
    [TestCase(KeyModifiers.Meta)]
    public void ScrollZoomUsesControlOrMacCommandAndLeavesPlainScrollingForPanning(KeyModifiers modifiers)
    {
        var canvas = new DiffLensControl();
        var initialZoom = canvas.Zoom;
        var wheel = new PointerWheelEventArgs(
            canvas, new Pointer(1, PointerType.Mouse, true), canvas, new Point(100, 80), 0,
            new PointerPointProperties(), modifiers, new Vector(0.25, 0.5));

        canvas.RaiseEvent(wheel);

        var zooms = modifiers == KeyModifiers.Control ||
                    (OperatingSystem.IsMacOS() && modifiers == KeyModifiers.Meta);
        Assert.That(canvas.Zoom, Is.EqualTo(zooms ? initialZoom * Math.Pow(1.15, 0.5) : initialZoom).Within(0.000001));
        Assert.That(wheel.Handled, Is.True);
    }

}
