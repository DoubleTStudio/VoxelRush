using NUnit.Framework;
using VoxelRush.Editor;

namespace VoxelRush.Tests.EditMode
{
    public sealed class ProjectConfigurationTests
    {
        [Test]
        public void CommittedProjectSettings_MatchProjectConfiguration()
        {
            var issues = ProjectConfiguration.Validate();

            Assert.That(issues, Is.Empty, "Run 'VoxelRush/Apply Project Configuration' and commit ProjectSettings:\n" + string.Join("\n", issues));
        }
    }
}
