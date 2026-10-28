using BiliIndexTranslate.Core.Geometry;

namespace BiliIndexTranslate.App.Popup;

/// <summary>Shared contract for non-activating floating popup windows managed by <see cref="PopupPresenter"/>.</summary>
internal interface IPopupSurface
{
    IntPtr Hwnd { get; }
    PxRect? CardPhysicalRect();
    void ScrollBy(int wheelDelta);
    void ApplyTextScale(double scale);
}
