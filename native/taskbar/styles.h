#pragma once
#include "protocol.h"
#include <unknwn.h>
#ifdef GetCurrentTime
#undef GetCurrentTime
#endif
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Data.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Shapes.h>
#include <vector>
#include <memory>
namespace magm {
namespace xaml=winrt::Windows::UI::Xaml;
enum class Kind { None, Frame, Content, Tray, Background, Stroke, Icon, Button, Widgets };
Kind classify(const std::wstring& type,const std::wstring& name,bool inTaskbar);
class StyledElement {
    struct Property {
        xaml::DependencyProperty key{nullptr};
        winrt::Windows::Foundation::IInspectable original{nullptr},desired{nullptr};
        xaml::Data::BindingBase binding{nullptr};
        int64_t token=0;
    };
    winrt::weak_ref<xaml::FrameworkElement> target;
    std::vector<std::shared_ptr<Property>> properties;
    std::shared_ptr<bool> writing=std::make_shared<bool>(false);
    void set(xaml::DependencyProperty const&,winrt::Windows::Foundation::IInspectable const&);
public:
    Kind kind;
    StyledElement(xaml::FrameworkElement const& element,Kind type):target(element),kind(type){}
    ~StyledElement();
    void apply(const Settings&);
    bool restore() noexcept;
};
}
