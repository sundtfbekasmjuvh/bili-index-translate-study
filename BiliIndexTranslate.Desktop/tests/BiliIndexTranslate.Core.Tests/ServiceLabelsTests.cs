using BiliIndexTranslate.Core.Settings;
using BiliIndexTranslate.Core.Translation;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class ServiceLabelsTests
{
    [Fact]
    public void ModelLabel_is_the_model_for_the_OpenAi_format()
        => Assert.Equal("my-model", ServiceLabels.ModelLabel(new AppSettings { ApiFormat = ApiFormat.OpenAi, Model = "my-model" }));

    [Fact]
    public void ModelLabel_is_DeepLX_for_the_Deeplx_format_whatever_the_model()
        => Assert.Equal(DeeplxProtocol.ModelLabel, ServiceLabels.ModelLabel(new AppSettings { ApiFormat = ApiFormat.Deeplx, Model = "ignored" }));

    [Fact]
    public void ModelLabel_is_the_template_name_for_a_custom_API()
        => Assert.Equal("My Service", ServiceLabels.ModelLabel(new AppSettings { ApiFormat = ApiFormat.Custom, CustomName = " My Service ", Model = "ignored" }));

    [Fact]
    public void ModelLabel_is_the_default_label_for_a_custom_API_without_a_name()
        => Assert.Equal(CustomApiProtocol.DefaultLabel, ServiceLabels.ModelLabel(new AppSettings { ApiFormat = ApiFormat.Custom, CustomName = "  " }));

    [Fact]
    public void HostOf_is_the_host_of_the_endpoint()
        => Assert.Equal("example.com", ServiceLabels.HostOf(new AppSettings { ApiFormat = ApiFormat.OpenAi, BaseUrl = "https://example.com/v1", UseRelay = false }));

    [Fact]
    public void HostOf_says_invalid_when_the_endpoint_does_not_resolve()
        => Assert.Equal("(invalid)", ServiceLabels.HostOf(new AppSettings { ApiFormat = ApiFormat.OpenAi, BaseUrl = "not a url", UseRelay = false }));
}
