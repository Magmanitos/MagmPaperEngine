#pragma once
#include "styles.h"
#include <winrt/Windows.UI.Composition.h>
#include <climits>

namespace magm {
namespace composition=winrt::Windows::UI::Composition;

class GlassVisual {
    winrt::weak_ref<xaml::FrameworkElement> element;
    composition::Compositor compositor{nullptr};
    composition::Visual previous{nullptr};
    composition::ContainerVisual root{nullptr};
    composition::SpriteVisual backdrop{nullptr};
    composition::CompositionRoundedRectangleGeometry outline{nullptr}, edge{nullptr}, innerEdge{nullptr};
    composition::CompositionLinearGradientBrush tint{nullptr}, sheen{nullptr}, rim{nullptr}, innerRim{nullptr};
    composition::ShapeVisual borders{nullptr};
    winrt::event_token sizeChanged{};
    UINT color=UINT_MAX, endColor=UINT_MAX, opacity=UINT_MAX, radius=UINT_MAX;

    explicit GlassVisual(xaml::FrameworkElement const& target);
    void arrange();
    void useBackdrop();
public:
    static std::shared_ptr<GlassVisual> create(xaml::FrameworkElement const& target);
    void apply(Settings const&);
    ~GlassVisual() noexcept;
};

double glassRadius(double height,UINT radius);
}
