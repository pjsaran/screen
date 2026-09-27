using System.Reflection;

using Shouldly;

namespace Captr.Integration.Tests;

/// <summary>
/// Every integration test class declares what it needs, as a known category.
/// </summary>
/// <remarks>
/// <para>
/// The build and CI once selected integration tests by INCLUDING the CI-safe
/// categories (<c>Category=Ffmpeg|Category=Chaos</c>). A test class with no category
/// matched nothing and so never ran anywhere — not locally, not in CI, not before a
/// release — while <c>testing.md</c> went on citing it as coverage. Fourteen tests
/// were in that state, including the SharePoint resume-from-offset test.
/// </para>
/// <para>
/// The build now EXCLUDES the categories a machine cannot run instead, so an
/// uncategorised test runs by default. This test closes the other half: a category
/// is a promise about what a test needs, so every class must make one, and it must
/// be one the build knows how to schedule.
/// </para>
/// </remarks>
[Trait("Category", "Os")]
public class TestCategoryTests
{
    /// <summary>The categories build.ps1 and CI know how to schedule.</summary>
    private static readonly string[] KnownCategories =
        ["Os", "Ffmpeg", "Chaos", "Published", "Display", "Gpu", "Soak", "Installer"];

    [Fact]
    public void Every_integration_test_class_declares_one_known_category()
    {
        IEnumerable<Type> testClasses = typeof(TestCategoryTests).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract)
            .Where(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Any(method => method.GetCustomAttributes<FactAttribute>(inherit: true).Any()));

        var problems = new List<string>();
        foreach (Type testClass in testClasses)
        {
            string[] categories = testClass.GetCustomAttributesData()
                .Where(data => data.AttributeType == typeof(TraitAttribute)
                               && data.ConstructorArguments.Count == 2
                               && (string?)data.ConstructorArguments[0].Value == "Category")
                .Select(data => (string)data.ConstructorArguments[1].Value!)
                .ToArray();

            if (categories.Length == 0)
            {
                problems.Add($"{testClass.FullName} has no [Trait(\"Category\", ...)]");
            }
            else if (categories.Any(category => !KnownCategories.Contains(category)))
            {
                problems.Add($"{testClass.FullName} uses an unknown category: {string.Join(", ", categories)}");
            }
        }

        problems.ShouldBeEmpty(
            "every integration test class needs exactly the categories build.ps1 schedules: " +
            string.Join(", ", KnownCategories));
    }
}
