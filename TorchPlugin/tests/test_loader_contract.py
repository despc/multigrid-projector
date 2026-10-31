"""Offline host/build contracts; live welding is a separate server acceptance gate."""
import pathlib
import unittest
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[2]


class LoaderContract(unittest.TestCase):
    def test_links_core_but_excludes_automatic_session_and_bootstrap(self):
        path = ROOT / 'TorchPlugin/TorchPlugin.csproj'
        self.assertTrue(path.exists(), 'Torch loader project is missing')
        tree = ET.parse(path)
        links = [x.attrib.get('Include', '') for x in tree.findall('.//Compile')]
        exclusions = ';'.join(x.attrib.get('Exclude', '') for x in tree.findall('.//Compile'))
        self.assertTrue(any('ServerPlugin' in x for x in links))
        self.assertIn('PluginSession.cs', exclusions)
        self.assertIn('Preloader.cs', exclusions)
        imports = [x.attrib.get('Project', '') for x in tree.findall('.//Import')]
        self.assertTrue(any('Shared.projitems' in x for x in imports))
        self.assertTrue(any('MultigridProjectorApi.projitems' in x for x in imports))
        self.assertEqual('net48', tree.findtext('.//TargetFramework'))
        self.assertEqual('MultigridProjector', tree.findtext('.//AssemblyName'))

    def test_backend_installs_only_after_original_guard(self):
        source = (ROOT / 'ServerPlugin/Plugin.cs').read_text()
        early = source.split('private static void EarlyStartup()', 1)[1]
        self.assertIn('MultigridProjectorTorch.TorchPatchBackend.Install()', early)
        self.assertLess(early.index('EnsureOriginal.VerifyAll()'), early.index('MultigridProjectorTorch.TorchPatchBackend.Install()'))
        self.assertLess(early.index('MultigridProjectorTorch.TorchPatchBackend.Install()'), early.index('PatchHelpers.PatchUncategorized(Harmony)'))
        self.assertIn('#if TORCH_LOADER', source)
        host = (ROOT / 'TorchPlugin/TorchPlugin.cs').read_text()
        loading = host.split('case TorchSessionState.Loading:', 1)[1].split('case TorchSessionState.Loaded:', 1)[0]
        self.assertIn('TorchPatchBackend.Manager', loading)
        self.assertLess(loading.index('TorchPatchBackend.Manager'), loading.index('plugin.Init(null)'))
        helpers = (ROOT / 'Shared/Utilities/PatchHelpers.cs').read_text()
        self.assertIn('TorchPatchBackend.PatchHarmony(harmony, null)', helpers)
        self.assertIn('TorchPatchBackend.PatchHarmony(harmony, category)', helpers)

    def test_host_defers_original_init_and_owns_one_session(self):
        path = ROOT / 'TorchPlugin/TorchPlugin.cs'
        self.assertTrue(path.exists(), 'Torch host entry point is missing')
        source = path.read_text()
        init = source.split('public override void Init(ITorchBase torch)', 1)[1].split('private void OnSessionStateChanged', 1)[0]
        self.assertNotIn('plugin.Init(', init)
        self.assertNotIn('InstallEarlyBootstrap', source)
        self.assertIn('case TorchSessionState.Loading:', source)
        self.assertIn('plugin.Init(null)', source)
        self.assertIn('case TorchSessionState.Loaded:', source)
        self.assertEqual(1, source.count('new MultigridProjectorSession()'))
        self.assertIn('mgpSession?.Update()', source)
        self.assertIn('mgpSession.Dispose()', source)
        self.assertIn('SessionStateChanged -= OnSessionStateChanged', source)
        self.assertIn('GetField("failed"', source)
        self.assertIn('OnEntityCreate', source)


if __name__ == '__main__':
    unittest.main()
