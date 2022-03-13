#if TK_ADDRESSABLE
using System.Linq;
using ThunderKit.Common;
using UnityEditor;
using UnityEngine;
using UnityEngine.AddressableAssets;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using ThunderKit.Core.Windows;
#if UNITY_2019_1_OR_NEWER
using UnityEngine.UIElements;
#else
using UnityEditor.Experimental.UIElements;
using UnityEngine.Experimental.UIElements;
#endif

namespace ThunderKit.RemoteAddressables
{
    public class AddressableBrowser : TemplatedWindow
    {
        [MenuItem(Constants.ThunderKitMenuRoot + "Addressable Browser")]
        public static void ShowAddressableBrowser() => GetWindow<AddressableBrowser>();

        public override string Title { get => ObjectNames.NicifyVariableName(GetType().Name); }

        private const string CopyButton = "addressable-copy-button";
        private const string NameLabel = "addressable-label";
        private const string PreviewIcon = "addressable-icon";
        private const string AddressableAssetName = "addressable-asset";
        private const string ButtonPanel = "addressable-button-panel";
        private const string LoadSceneButton = "addressable-load-scene-button";
        List<string> CatalogDirectories;
        Dictionary<string, List<string>> DirectoryContents;
        private ListView directory;
        private ListView directoryContent;
        private Texture sceneIcon;
        private TextField searchBox;

        public bool caseSensitive;
        public string searchInput;

        public override void OnEnable()
        {
            base.OnEnable();
            sceneIcon = EditorGUIUtility.IconContent("d_UnityLogo").image;

            searchBox = rootVisualElement.Q<TextField>("search-input");
            directory = rootVisualElement.Q<ListView>("directory");
            directoryContent = rootVisualElement.Q<ListView>("directory-content");

            directory.makeItem = DirectoryLabel;
            directoryContent.makeItem = AssetLabel;

            directory.bindItem = (element, i) =>
            {
                Label label = element.Q<Label>(NameLabel);
                var address = (string)directory.itemsSource[i];
                label.text = address;
            };
            directoryContent.bindItem = BindAsset;

            var allKeys = Addressables.ResourceLocators.SelectMany(locator => locator.Keys).Select(key => key.ToString());

            var allGroups = allKeys.GroupBy(key => Path.GetDirectoryName(key));
            DirectoryContents = allGroups.ToDictionary(g => g.Key, g => g.OrderBy(k => k).ToList());
            CatalogDirectories = DirectoryContents.Keys.OrderBy(k => k).ToList();
            var scenes = allKeys.Where(key => key.EndsWith(".unity")).ToList();
            CatalogDirectories.Insert(0, "Scenes");
            DirectoryContents["Scenes"] = scenes;

            directory.itemsSource = CatalogDirectories;

            directory.Refresh();
            directory.onSelectionChanged += Directory_onSelectionChanged;
            directoryContent.onSelectionChanged += DirectoryContent_onSelectionChanged;
#if UNITY_2019_1_OR_NEWER
            searchBox.RegisterValueChangedCallback(OnSearchChanged);
#else 
            searchBox.OnValueChanged(OnSearchChanged);
#endif
        }

        private void OnSearchChanged(ChangeEvent<string> evt)
        {
            bool noFilter = string.IsNullOrEmpty(evt.newValue);
            if (noFilter)
                directory.itemsSource = CatalogDirectories;
            else
            {
                var matches = DirectoryContents.Where(kvp => kvp.Value.Any(v => CompareSearch(evt.newValue, v))).ToArray();
                directory.itemsSource = matches.Select(kvp => kvp.Key).ToList();
            }
        }

        struct KeyData
        {
            public string name;
            public string type;
            public string address;

            public System.Type Type => System.Type.GetType(type);
        }
        HashSet<KeyData> DataCache = new HashSet<KeyData>();
        void BuildDataCache()
        {
            foreach (var item in Addressables.ResourceLocators)
            {
                foreach (var key in item.Keys)
                {
                    try
                    {
                        var assetOp = Addressables.LoadAssetAsync<Object>(key);
                        assetOp.Completed += obj =>
                        {
                            var result = obj.Result;
                            DataCache.Add(new KeyData
                            {
                                name = result.name,
                                type = result.GetType().AssemblyQualifiedName,
                                address = key.ToString()
                            });
                        };
                    }
                    catch { }
                }
            }
        }


        async void BindAsset(VisualElement element, int i)
        {
            var icon = element.Q<Image>(PreviewIcon);
            var label = element.Q<Label>(NameLabel);
            var copyBtn = element.Q<Button>(CopyButton);
            var loadSceneBtn = element.Q<Button>(LoadSceneButton);
            copyBtn.clickable = new Clickable(() =>
            {
                var text = directoryContent.itemsSource[i] as string;
                EditorGUIUtility.systemCopyBuffer = text;
            });
            var address = (string)directoryContent.itemsSource[i];
            label.text = address;

            icon.image = null;
            if (!address.EndsWith(".unity"))
            {
                var texture = await RenderIcon(address);
                if (texture)
                {
                    icon.image = texture;
                    Repaint();
                }
            }
            else
                icon.image = sceneIcon;

            if (address.EndsWith(".unity") && EditorApplication.isPlaying)
            {
                loadSceneBtn.RemoveFromClassList("hidden");
                loadSceneBtn.clickable = new Clickable(() =>
                {
                    var text = directoryContent.itemsSource[i] as string;
                    Addressables.LoadSceneAsync(text);
                });
            }
            else
                loadSceneBtn.AddToClassList("hidden");
        }

        private async Task<Texture> RenderIcon(string address)
        {
            Texture preview = null;
            Object result = null;
            try
            {
                if (!address.EndsWith(".unity"))
                {
                    var loadOperation = Addressables.LoadAssetAsync<Object>(address);
                    await loadOperation.Task;
                    result = loadOperation.Result;
                    preview = UpdatePreview(result);
                }
            }
            catch { }
            if (result)
                while (AssetPreview.IsLoadingAssetPreviews())
                {
                    await Task.Delay(500);
                    preview = UpdatePreview(result);
                    try
                    {
                        Addressables.Release(result);
                    }
                    catch { }
                }

            return preview;
        }

        private static Texture UpdatePreview(Object result)
        {
            Texture preview;
            switch (result)
            {
                case GameObject gobj when gobj.GetComponentsInChildren<SkinnedMeshRenderer>().Any()
                                       || gobj.GetComponentsInChildren<SpriteRenderer>().Any()
                                       || gobj.GetComponentsInChildren<MeshRenderer>().Any()
                                       || gobj.GetComponentsInChildren<CanvasRenderer>().Any():
                case Material mat:
                    preview = AssetPreview.GetAssetPreview(result);
                    break;
                default:
                    preview = AssetPreview.GetMiniThumbnail(result);
                    break;
            }

            return preview;
        }

        private async void DirectoryContent_onSelectionChanged(List<object> obj)
        {
            try
            {
                var first = obj.OfType<string>().First();
                if (first.EndsWith(".unity")) return;
                var firstOp = Addressables.LoadAssetAsync<Object>(first);
                var firstObj = await firstOp.Task;
                firstObj.hideFlags |= HideFlags.NotEditable;
                Selection.activeObject = firstObj;
            }
            catch { }
        }

        VisualElement DirectoryLabel() => new Label { name = NameLabel };
        VisualElement AssetLabel()
        {
            var element = new VisualElement { name = AddressableAssetName };
            element.Add(new Image { name = PreviewIcon });
            element.Add(new Label { name = NameLabel });

            var buttonPanel = new VisualElement { name = ButtonPanel };
            buttonPanel.Add(new Button { name = CopyButton, text = "Copy Address" });

            var loadSceneBtn = new Button { name = LoadSceneButton, text = "Load Scene" };
            loadSceneBtn.AddToClassList("hidden");

            buttonPanel.Add(loadSceneBtn);
            element.Add(buttonPanel);

            return element;
        }

        private void Directory_onSelectionChanged(List<object> obj)
        {
            var selected = obj.First().ToString();
            var addresses = DirectoryContents[selected];
            var matches = addresses.Where(address => CompareSearch(searchInput, address)).ToArray();
            directoryContent.itemsSource = matches;
        }

        bool CompareSearch(string search, string value)
        {
            var filter = search;
            var input = value;

            if (!caseSensitive)
            {
                if (!string.IsNullOrEmpty(filter))
                    filter = filter.ToLowerInvariant();
                input = input.ToLowerInvariant();
            }

            return string.IsNullOrEmpty(filter) ? true : input.Contains(filter);
        }
    }
}
#endif