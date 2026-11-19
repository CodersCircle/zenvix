using Zenvix.ViewModels;

namespace Zenvix.Tests;

public class SlugifyTests
{
    [Theory]
    [InlineData("HTML/CSS", "html-css")]          // a "/" produced the domain my-html/css.zenvix and broke deploys
    [InlineData("new_penal", "new-penal")]        // "_" is invalid in hostnames and SSL certificates
    [InlineData("  My Laravel App ", "my-laravel-app")]
    [InlineData("Vue.js 3", "vue-js-3")]
    [InlineData("--a__b--", "a-b")]
    [InlineData("", "")]
    public void Slugify_produces_safe_host_labels(string input, string expected)
        => Assert.Equal(expected, WebsitesViewModel.Slugify(input));
}
