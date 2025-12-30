using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/* TODO

Future ideas: 
 * Drag & drop assets into window
 * Group nodes to minimize connections crossing
 * Add color coding for different asset types
 * Use Unity's styles instead of hardcoded UI coloring for better integration
 * Add filter options to exclude specific asset types (built-in assets, packages, scripts, etc.)
 * Tooltip style (black outline, grey background like Animator)
 * Text box width depending on spacing
 * Balance memory usage vs performance for very large projects
 * Add minimap for large graphs
 
Known code smells:
 * God-object needs to be split up
 * Hardcoded UI strings
 * Inconsistent error handling/logging -> this is intentional, only show user-facing errors!
 
NTH (thx Claude):
 * Add search/filter functionality within the graph
 * Add export functionality (save graph as image or data)
 * Optimize reverse dependency scanning for very large projects
 */

namespace bnj.dependency_graph.Editor
{
    public class DependencyGraphWindow : EditorWindow
    {
        #region Fields
        private string _targetAssetPath;
        private DependencyGraphData _graphData;
        private Vector2 _graphOffset = Vector2.zero;
        private float _zoom = 1f;
        private const float MinZoom = 0.5f;
        private const float MaxZoom = 2f;
        private const float ZoomSensitivity = 0.05f;

        // UI Settings
        private const float PreviewSize = 64f;
        private int _maxDepth = 5;
        private const int MinDepth = 5;
        private int _calculatedMaxDepth = 5;

        // Layout
        private const float MinNodeSpacingX = 80f;
        private const float MaxNodeSpacingX = 300f;
        private const float NodeSpacingY = 240f;
        private const float LabelHeight = 30f;
        private const float LabelSpacing = 8f;
        private const float LabelWidthMultiplier = 1.2f;
        private const float BezierTangentStrength = 100f;
        private const float ConnectionLineWidth = 6f;
        private const float TooltipOffsetX = 15f;
        private const float TooltipOffsetY = 15f;
        private const float TooltipMaxWidthRatio = 0.5f;
        private const int FallbackTextLength = 20;

        // Colors
        private static readonly Color DependencyLineColor = new(0.4f, 0.75f, 1f, 1f);
        private static readonly Color DependentLineColor = new(1f, 0.75f, 0.4f, 1f);
        private static readonly Color SelectedNodeColor = new(1f, 1f, 0.3f, 0.8f);
        private static readonly Color NodeBackgroundColor = new(0.2f, 0.2f, 0.2f, 0.9f);

        // Grid
        private const float MinGridSpacing = 5f;
        private const float GridSpacingSmall = 20f;
        private const float GridSpacingLarge = 100f;
        private static readonly Color GridColorSmall = new(0.2f, 0.2f, 0.2f, 0.4f);
        private static readonly Color GridColorLarge = new(0.3f, 0.3f, 0.3f, 0.6f);

        // Interaction
        private DependencyNode _hoveredNode;
        private DependencyNode _selectedNode;
        private string _currentTooltip;

        // Caching
        private Dictionary<string, Texture2D> _previewCache = new();
        private Dictionary<string, List<string>> _reverseDependencyCache;
        private bool _isCacheValid = false;
        private Texture2D _solidColorTexture;
        #endregion

        #region Menu Items
        /// <summary>
        /// Opens the Dependency Graph window for the currently selected asset in the Project view.
        /// Shows both dependencies (what this asset needs) and dependents (what references this asset).
        /// </summary>
        [MenuItem("Assets/Show Dependency Graph", false, 30)]
        private static void ShowDependencyGraph()
        {
            var selectedAsset = Selection.activeObject;
            if (selectedAsset == null)
            {
                Debug.LogWarning("No asset selected!");
                return;
            }

            var assetPath = AssetDatabase.GetAssetPath(selectedAsset);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogWarning("Selected object is not an asset!");
                return;
            }

            var window = GetWindow<DependencyGraphWindow>("Dependency Graph");
            window.ShowGraphForAsset(assetPath);
            window.Show();
        }

        [MenuItem("Assets/Show Dependency Graph", true)]
        private static bool ShowDependencyGraphValidate()
        {
            return Selection.activeObject != null;
        }

        #endregion

        #region Initialization
        private void ShowGraphForAsset(string assetPath)
        {
            _targetAssetPath = assetPath;
            _graphOffset = Vector2.zero;
            _zoom = 1f;
            _previewCache.Clear();
            _currentTooltip = null;

            if (!_isCacheValid)
            {
                BuildReverseDependencyCache();
            }

            BuildDependencyGraph();
        }

        private void OnEnable()
        {
            EditorApplication.projectChanged += OnProjectChanged;
            AssemblyReloadEvents.afterAssemblyReload += OnAssemblyReload;
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= OnProjectChanged;
            AssemblyReloadEvents.afterAssemblyReload -= OnAssemblyReload;

            if (_solidColorTexture != null)
            {
                DestroyImmediate(_solidColorTexture);
                _solidColorTexture = null;
            }
        }

        private void OnProjectChanged()
        {
            InvalidateCache();
        }

        private void OnAssemblyReload()
        {
            InvalidateCache();
        }

        private void InvalidateCache()
        {
            _isCacheValid = false;
            _reverseDependencyCache = null;
        }
        #endregion

        #region Cache Building
        private void BuildReverseDependencyCache()
        {
            var startTime = EditorApplication.timeSinceStartup;
            _reverseDependencyCache = new Dictionary<string, List<string>>();

            var allAssetPaths = AssetDatabase.GetAllAssetPaths()
                .Where(path => !string.IsNullOrEmpty(path) && path.StartsWith("Assets/"))
                .ToArray();

            var totalAssets = allAssetPaths.Length;
            var progressUpdateInterval = Mathf.Max(1, totalAssets / 100);
            var shouldShowProgress = totalAssets > 50;

            try
            {
                for (int i = 0; i < totalAssets; i++)
                {
                    if (shouldShowProgress && i % progressUpdateInterval == 0)
                    {
                        var progress = (float)i / totalAssets;
                        if (EditorUtility.DisplayCancelableProgressBar(
                            "Building Dependency Cache",
                            $"Scanning assets... ({i}/{totalAssets})",
                            progress))
                        {
                            Debug.LogWarning("Dependency cache building cancelled by user");
                            return;
                        }
                    }

                    var assetPath = allAssetPaths[i];
                    var dependencies = AssetDatabase.GetDependencies(assetPath, false);

                    foreach (var dependency in dependencies)
                    {
                        if (dependency == assetPath) continue;

                        if (!_reverseDependencyCache.TryGetValue(dependency, out var dependentList))
                        {
                            dependentList = new List<string>();
                            _reverseDependencyCache[dependency] = dependentList;
                        }

                        if (!dependentList.Contains(assetPath))
                        {
                            dependentList.Add(assetPath);
                        }
                    }
                }

                _isCacheValid = true;
                var elapsedTime = EditorApplication.timeSinceStartup - startTime;
                Debug.Log($"Dependency cache built successfully in {elapsedTime:F2} seconds. Scanned {totalAssets} assets.");
            }
            finally
            {
                if (shouldShowProgress)
                {
                    EditorUtility.ClearProgressBar();
                }
            }
        }
        #endregion

        #region Graph Building
        private void BuildDependencyGraph()
        {
            if (string.IsNullOrEmpty(_targetAssetPath))
                return;

            _graphData = new();

            var rootNode = new DependencyNode
            {
                AssetPath = _targetAssetPath,
                Depth = 0,
                IsRoot = true,
                Position = new(position.width / 2f - PreviewSize / 2f, position.height / 2f - PreviewSize / 2f)
            };

            _graphData.Nodes.Add(rootNode);
            _graphData.NodesByPath[_targetAssetPath] = rootNode;

            BuildDependencies(rootNode, _maxDepth);
            BuildDependents(rootNode, _maxDepth);

            CalculateRequiredDepth();

            LayoutGraph();
            Repaint();
        }

        private void BuildDependencies(DependencyNode node, int remainingDepth)
        {
            if (remainingDepth <= 0) return;

            var dependencies = AssetDatabase.GetDependencies(node.AssetPath, false)
                .Where(dep => dep != node.AssetPath)
                .Where(dep => !dep.EndsWith(".dll"))
                .ToArray();

            BuildConnections(node, dependencies, remainingDepth, isDependent: false);
        }

        private void BuildDependents(DependencyNode node, int remainingDepth)
        {
            if (remainingDepth <= 0) return;
            if (_reverseDependencyCache == null || !_reverseDependencyCache.ContainsKey(node.AssetPath))
                return;

            var dependents = _reverseDependencyCache[node.AssetPath].ToArray();

            BuildConnections(node, dependents, remainingDepth, isDependent: true);
        }

        private void BuildConnections(DependencyNode node, string[] assetPaths, int remainingDepth, bool isDependent)
        {
            foreach (var assetPath in assetPaths)
            {
                if (assetPath == node.AssetPath) continue;

                if (_graphData.NodesByPath.TryGetValue(assetPath, out DependencyNode connectedNode))
                {
                    var connectionList = isDependent ? node.Dependents : node.Dependencies;
                    if (!connectionList.Contains(connectedNode))
                    {
                        connectionList.Add(connectedNode);
                    }
                }
                else
                {
                    connectedNode = new()
                    {
                        AssetPath = assetPath,
                        Depth = isDependent ? node.Depth + 1 : node.Depth - 1,
                        IsRoot = false
                    };

                    _graphData.Nodes.Add(connectedNode);
                    _graphData.NodesByPath[assetPath] = connectedNode;

                    var connectionList = isDependent ? node.Dependents : node.Dependencies;
                    connectionList.Add(connectedNode);

                    if (isDependent)
                        BuildDependents(connectedNode, remainingDepth - 1);
                    else
                        BuildDependencies(connectedNode, remainingDepth - 1);
                }
            }
        }

        private void CalculateRequiredDepth()
        {
            if (_graphData.Nodes.Count == 0)
            {
                _calculatedMaxDepth = MinDepth;
                return;
            }

            var minDepth = _graphData.Nodes.Min(n => n.Depth);
            var maxDepth = _graphData.Nodes.Max(n => n.Depth);

            _calculatedMaxDepth = Mathf.Max(Mathf.Abs(minDepth), Mathf.Abs(maxDepth));
            _calculatedMaxDepth = Mathf.Max(_calculatedMaxDepth, MinDepth);
        }
        #endregion

        #region Layout
        private void LayoutGraph()
        {
            var nodesByDepth = new Dictionary<int, List<DependencyNode>>();

            foreach (var node in _graphData.Nodes)
            {
                if (!nodesByDepth.ContainsKey(node.Depth))
                {
                    nodesByDepth[node.Depth] = new();
                }
                nodesByDepth[node.Depth].Add(node);
            }

            var sortedDepths = nodesByDepth.Keys.OrderBy(d => d).ToList();
            var centerX = position.width / 2f;
            var centerY = position.height / 2f;

            foreach (var depth in sortedDepths)
            {
                var nodesAtDepth = nodesByDepth[depth];
                var nodeCount = nodesAtDepth.Count;

                var spacingInterpolation = Mathf.Clamp01((nodeCount - 2f) / 4f);
                var spacing = Mathf.Lerp(MaxNodeSpacingX, MinNodeSpacingX, spacingInterpolation);

                var totalWidth = (nodeCount - 1) * spacing;
                var startX = centerX - totalWidth / 2f;
                var y = centerY + depth * NodeSpacingY;

                for (int i = 0; i < nodeCount; i++)
                {
                    var node = nodesAtDepth[i];
                    node.Position = new(startX + i * spacing, y);
                }
            }
        }
        #endregion

        #region GUI
        private void OnGUI()
        {
            if (_graphData == null || string.IsNullOrEmpty(_targetAssetPath))
            {
                DrawEmptyState();
                return;
            }

            DrawToolbar();
            DrawGraph();
            HandleInput();
            DrawTooltip();
        }

        private void DrawEmptyState()
        {
            GUILayout.BeginArea(new(0, 0, position.width, position.height));
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label("Right-click an asset and select 'Show Dependency Graph'", EditorStyles.centeredGreyMiniLabel);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.EndArea();
        }

        private void DrawToolbar()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.FlexibleSpace();

            // Show depth control only if calculated depth exceeds min depth
            if (_calculatedMaxDepth > MinDepth)
            {
                GUILayout.Label("Max Depth:", GUILayout.Width(70));
                var newMaxDepth = EditorGUILayout.IntSlider(_maxDepth, MinDepth, _calculatedMaxDepth, GUILayout.Width(150));
                if (newMaxDepth != _maxDepth)
                {
                    _maxDepth = newMaxDepth;
                    BuildDependencyGraph();
                }

                GUILayout.Space(10);
            }

            if (GUILayout.Button("Rebuild Cache", EditorStyles.toolbarButton, GUILayout.Width(100)))
            {
                InvalidateCache();
                BuildReverseDependencyCache();
                BuildDependencyGraph();
            }

            if (GUILayout.Button("Reset View", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                _graphOffset = Vector2.zero;
                _zoom = 1f;
                Repaint();
            }

            GUILayout.EndHorizontal();
        }

        private void DrawGraph()
        {
            var graphRect = new Rect(0, EditorStyles.toolbar.fixedHeight, position.width, position.height - EditorStyles.toolbar.fixedHeight);

            // Background
            EditorGUI.DrawRect(graphRect, new(0.15f, 0.15f, 0.15f, 1f));

            // Begin scrollable area with zoom
            GUI.BeginGroup(graphRect);

            var zoomedOffset = _graphOffset * _zoom;

            // Draw grid
            DrawBackgroundGrid(graphRect, zoomedOffset);

            // Draw connections first (behind nodes)
            foreach (var node in _graphData.Nodes)
            {
                // Draw dependency connections (upward)
                foreach (var dependency in node.Dependencies)
                {
                    DrawConnection(node.Position, dependency.Position, DependencyLineColor, zoomedOffset);
                }

                // Draw dependent connections (downward)
                foreach (var dependent in node.Dependents)
                {
                    DrawConnection(node.Position, dependent.Position, DependentLineColor, zoomedOffset);
                }
            }

            // Draw nodes
            _hoveredNode = null;
            foreach (var node in _graphData.Nodes)
            {
                DrawNode(node, zoomedOffset, graphRect);
            }

            GUI.EndGroup();

            // Update tooltip based on hover state
            if (_hoveredNode != null)
            {
                _currentTooltip = _hoveredNode.AssetPath;
            }
            else
            {
                _currentTooltip = null;
            }
        }

        private void DrawConnection(Vector2 from, Vector2 to, Color color, Vector2 offset)
        {
            var start = from * _zoom + offset + new Vector2(PreviewSize * _zoom / 2f, PreviewSize * _zoom / 2f);
            var end = to * _zoom + offset + new Vector2(PreviewSize * _zoom / 2f, PreviewSize * _zoom / 2f);

            var isUpward = end.y < start.y;
            var tangentStrength = BezierTangentStrength * _zoom;
            var startTangent = start + (isUpward ? Vector2.down : Vector2.up) * tangentStrength;
            var endTangent = end + (isUpward ? Vector2.up : Vector2.down) * tangentStrength;

            Handles.BeginGUI();
            Handles.DrawBezier(start, end, startTangent, endTangent, color, null, ConnectionLineWidth);
            Handles.EndGUI();
        }

        private void DrawNode(DependencyNode node, Vector2 offset, Rect graphRect)
        {
            var nodeSize = PreviewSize * _zoom;
            var labelHeight = LabelHeight * _zoom;
            var labelSpacing = LabelSpacing * _zoom;

            var nodeRect = CalculateNodeRect(node, offset, nodeSize, labelSpacing, labelHeight);

            if (!nodeRect.Overlaps(graphRect))
                return;

            var previewRect = new Rect(nodeRect.x, nodeRect.y, nodeSize, nodeSize);
            var labelRect = CalculateLabelRect(previewRect, nodeSize, labelSpacing, labelHeight);

            HandleNodeHover(node, previewRect);
            DrawNodeBorder(previewRect);
            DrawNodeBackground(node, previewRect);
            DrawNodePreview(node, previewRect);
            DrawNodeLabel(node, labelRect, nodeSize);
        }

        private Rect CalculateNodeRect(DependencyNode node, Vector2 offset, float nodeSize, float labelSpacing, float labelHeight)
        {
            return new Rect(
                node.Position.x * _zoom + offset.x,
                node.Position.y * _zoom + offset.y,
                nodeSize,
                nodeSize + labelSpacing + labelHeight
            );
        }

        private Rect CalculateLabelRect(Rect previewRect, float nodeSize, float labelSpacing, float labelHeight)
        {
            var labelWidth = nodeSize * LabelWidthMultiplier;
            return new Rect(
                previewRect.x - (labelWidth - nodeSize) / 2f,
                previewRect.y + nodeSize + labelSpacing,
                labelWidth,
                labelHeight
            );
        }

        private void HandleNodeHover(DependencyNode node, Rect previewRect)
        {
            if (previewRect.Contains(Event.current.mousePosition))
            {
                _hoveredNode = node;
            }
        }

        private void DrawNodeBorder(Rect previewRect)
        {
            // 1px black border
            EditorGUI.DrawRect(
                new Rect(previewRect.x - 1, previewRect.y - 1, previewRect.width + 2, previewRect.height + 2),
                Color.black
            );
        }

        private void DrawNodeBackground(DependencyNode node, Rect previewRect)
        {
            var backgroundColor = GetNodeColor(node);
            EditorGUI.DrawRect(
                new Rect(previewRect.x - 2, previewRect.y - 2, previewRect.width + 4, previewRect.height + 4),
                backgroundColor
            );
        }

        private void DrawNodePreview(DependencyNode node, Rect previewRect)
        {
            var preview = GetAssetPreview(node.AssetPath);
            if (preview != null)
            {
                GUI.DrawTexture(previewRect, preview, ScaleMode.ScaleToFit);
            }
            else
            {
                EditorGUI.DrawRect(previewRect, new Color(0.3f, 0.3f, 0.3f, 1f));
            }
        }

        private void DrawNodeLabel(DependencyNode node, Rect labelRect, float nodeSize)
        {
            var assetName = System.IO.Path.GetFileNameWithoutExtension(node.AssetPath);
            var labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(10 * _zoom),
                alignment = TextAnchor.UpperCenter,
                normal = { textColor = Color.white },
                wordWrap = false,
                clipping = TextClipping.Clip
            };

            var truncatedName = TruncateTextToWidth(assetName, labelRect.width, labelStyle, truncateFromStart: false);
            GUI.Label(labelRect, truncatedName, labelStyle);
        }

        private void DrawBackgroundGrid(Rect graphRect, Vector2 offset)
        {
            Handles.BeginGUI();

            var gridColor = EditorGUIUtility.isProSkin ? GridColorSmall : new Color(0.5f, 0.5f, 0.5f, 0.3f);
            var gridColorLarge = EditorGUIUtility.isProSkin ? GridColorLarge : new Color(0.4f, 0.4f, 0.4f, 0.5f);

            // Calculate grid spacing with zoom
            var smallSpacing = GridSpacingSmall * _zoom;
            var largeSpacing = GridSpacingLarge * _zoom;

            // Only draw grid if spacing is large enough to be visible
            if (smallSpacing < MinGridSpacing) return;

            // Calculate grid offset (wrap around)
            var offsetX = offset.x % smallSpacing;
            var offsetY = offset.y % smallSpacing;

            // Draw small grid lines with explicit thickness
            Handles.color = gridColor;

            // Vertical lines
            for (float x = offsetX; x < graphRect.width; x += smallSpacing)
            {
                Handles.DrawLine(new Vector3(x, 0, 0), new Vector3(x, graphRect.height, 0), 1f);
            }

            // Horizontal lines
            for (float y = offsetY; y < graphRect.height; y += smallSpacing)
            {
                Handles.DrawLine(new Vector3(0, y, 0), new Vector3(graphRect.width, y, 0), 1f);
            }

            // Draw large grid lines with thicker lines
            var largeOffsetX = offset.x % largeSpacing;
            var largeOffsetY = offset.y % largeSpacing;

            Handles.color = gridColorLarge;

            // Vertical lines
            for (float x = largeOffsetX; x < graphRect.width; x += largeSpacing)
            {
                Handles.DrawLine(new Vector3(x, 0, 0), new Vector3(x, graphRect.height, 0), 2f);
            }

            // Horizontal lines
            for (float y = largeOffsetY; y < graphRect.height; y += largeSpacing)
            {
                Handles.DrawLine(new Vector3(0, y, 0), new Vector3(graphRect.width, y, 0), 2f);
            }

            Handles.EndGUI();
        }

        private void DrawTooltip()
        {
            if (string.IsNullOrEmpty(_currentTooltip))
                return;

            var asset = AssetDatabase.LoadAssetAtPath<Object>(_currentTooltip);
            var assetType = asset != null ? asset.GetType().Name : "Unknown";
            var fileInfo = new System.IO.FileInfo(_currentTooltip);
            var fileSize = fileInfo.Exists ? FormatFileSize(fileInfo.Length) : "N/A";

            var maxWidth = position.width * TooltipMaxWidthRatio;
            var tooltipFontSize = 11;

            var truncateStyle = new GUIStyle(GUI.skin.box) { fontSize = tooltipFontSize };
            var displayPath = TruncateTextToWidth(_currentTooltip, maxWidth, truncateStyle, truncateFromStart: true);
            var displayType = TruncateTextToWidth(assetType, maxWidth, truncateStyle, truncateFromStart: false);
            var tooltipText = $"{displayPath}\nType: {displayType}\nSize: {fileSize}";

            var tooltipStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
                normal = {
                    textColor = Color.white,
                    background = GetOrCreateSolidColorTexture(Color.black)
                },
                border = new RectOffset(1, 1, 1, 1),
                padding = new RectOffset(8, 8, 6, 6)
            };

            var mousePos = Event.current.mousePosition;
            var content = new GUIContent(tooltipText);
            var tooltipSize = tooltipStyle.CalcSize(content);

            var tooltipRect = new Rect(
                mousePos.x + TooltipOffsetX,
                mousePos.y + TooltipOffsetY,
                tooltipSize.x,
                tooltipSize.y
            );
            tooltipRect = ClampTooltipToWindow(tooltipRect, mousePos);

            EditorGUI.DrawRect(new Rect(tooltipRect.x - 1, tooltipRect.y - 1, tooltipRect.width + 2, tooltipRect.height + 2), Color.black);

            GUI.Box(tooltipRect, tooltipText, tooltipStyle);
        }

        private Color GetNodeColor(DependencyNode node)
        {
            var baseColor = node.IsRoot ? SelectedNodeColor : NodeBackgroundColor;

            if (_hoveredNode == node)
                return Color.Lerp(baseColor, Color.white, 0.2f);

            if (_selectedNode == node && !node.IsRoot)
                return Color.Lerp(baseColor, SelectedNodeColor, 0.5f);

            return baseColor;
        }

        private string TruncateTextToWidth(string text, float maxWidth, GUIStyle style, bool truncateFromStart = false)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var content = new GUIContent(text);
            var textWidth = style.CalcSize(content).x;

            if (textWidth <= maxWidth)
                return text;

            const string ellipsis = "...";
            var ellipsisWidth = style.CalcSize(new GUIContent(ellipsis)).x;
            var targetWidth = maxWidth - ellipsisWidth;

            if (targetWidth <= 0)
                return ellipsis;

            int left = 0;
            int right = text.Length;
            int bestLength = 0;

            while (left <= right)
            {
                int mid = (left + right) / 2;

                string testText;
                if (truncateFromStart)
                {
                    testText = text.Substring(text.Length - mid);
                }
                else
                {
                    testText = text.Substring(0, mid);
                }

                var testWidth = style.CalcSize(new GUIContent(testText)).x;

                if (testWidth <= targetWidth)
                {
                    bestLength = mid;
                    left = mid + 1;
                }
                else
                {
                    right = mid - 1;
                }
            }

            if (bestLength == 0)
                return ellipsis + text.Substring(text.Length - Math.Min(FallbackTextLength, text.Length));

            if (truncateFromStart)
            {
                return ellipsis + text.Substring(text.Length - bestLength);
            }
            else
            {
                return text.Substring(0, bestLength) + ellipsis;
            }
        }

        private Rect ClampTooltipToWindow(Rect tooltipRect, Vector2 mousePos)
        {
            if (tooltipRect.xMax > position.width)
                tooltipRect.x = position.width - tooltipRect.width - 5;

            if (tooltipRect.yMax > position.height)
                tooltipRect.y = mousePos.y - tooltipRect.height - 5;

            return tooltipRect;
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private Texture2D GetOrCreateSolidColorTexture(Color color)
        {
            if (_solidColorTexture == null)
            {
                _solidColorTexture = new Texture2D(1, 1);
                _solidColorTexture.SetPixel(0, 0, color);
                _solidColorTexture.Apply();
                _solidColorTexture.hideFlags = HideFlags.DontSave;
            }

            return _solidColorTexture;
        }

        private Texture2D GetAssetPreview(string assetPath)
        {
            if (_previewCache.TryGetValue(assetPath, out var cached))
                return cached;

            var asset = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
            if (asset == null)
                return null;

            var preview = AssetPreview.GetAssetPreview(asset);
            if (preview == null)
            {
                preview = AssetPreview.GetMiniThumbnail(asset);
            }

            _previewCache[assetPath] = preview;
            return preview;
        }
        #endregion

        #region Input Handling
        private void HandleInput()
        {
            var e = Event.current;

            // Panning
            if (e.type == EventType.MouseDrag && (e.button == 2 || (e.button == 0 && e.alt)))
            {
                _graphOffset += e.delta / _zoom;
                e.Use();
                Repaint();
            }

            // Zooming
            if (e.type == EventType.ScrollWheel)
            {
                var oldZoom = _zoom;
                var zoomDelta = -e.delta.y * ZoomSensitivity;
                _zoom = Mathf.Clamp(_zoom + zoomDelta, MinZoom, MaxZoom);

                // Adjust offset to zoom towards cursor position
                var mousePos = e.mousePosition;
                var graphRect = new Rect(0, EditorStyles.toolbar.fixedHeight, position.width, position.height - EditorStyles.toolbar.fixedHeight);
                var mouseInGraph = mousePos - new Vector2(0, EditorStyles.toolbar.fixedHeight);

                // Calculate world position of mouse before zoom
                var worldPosBefore = (mouseInGraph - _graphOffset * oldZoom) / oldZoom;
                // Calculate world position of mouse after zoom
                var worldPosAfter = (mouseInGraph - _graphOffset * _zoom) / _zoom;
                // Adjust offset to keep world position under cursor the same
                _graphOffset += (worldPosAfter - worldPosBefore);

                e.Use();
                Repaint();
            }

            // Fullscreen toggle
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Space && e.shift)
            {
                maximized = !maximized;
                e.Use();
                return;
            }

            // Context menu
            if (e.type == EventType.MouseDown && e.button == 1 && _hoveredNode != null)
            {
                ShowNodeContextMenu(_hoveredNode);
                e.Use();
            }

            // Selection
            if (e.type == EventType.MouseDown && e.button == 0 && _hoveredNode != null)
            {
                _selectedNode = _hoveredNode;
                e.Use();
                Repaint();
            }

            // Always repaint if we have a tooltip to show smooth following
            if (!string.IsNullOrEmpty(_currentTooltip))
            {
                Repaint();
            }
        }

        private void ShowNodeContextMenu(DependencyNode node)
        {
            var menu = new GenericMenu();

            AddMenuItem(menu, "Ping in Project", () => PingAsset(node.AssetPath));
            AddMenuItem(menu, "Re-center Graph on This Asset", () => ShowGraphForAsset(node.AssetPath));
            menu.AddSeparator("");
            AddMenuItem(menu, "Select in Project", () => SelectAsset(node.AssetPath));

            menu.ShowAsContext();
        }

        private void AddMenuItem(GenericMenu menu, string label, Action action)
        {
            menu.AddItem(new GUIContent(label), false, () => action());
        }

        private void PingAsset(string assetPath)
        {
            var asset = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
            EditorGUIUtility.PingObject(asset);
        }

        private void SelectAsset(string assetPath)
        {
            var asset = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
            Selection.activeObject = asset;
        }
        #endregion
    }

    #region Data Structures
    [Serializable]
    public class DependencyGraphData
    {
        public List<DependencyNode> Nodes { get; init; } = new();
        public Dictionary<string, DependencyNode> NodesByPath { get; init; } = new();
    }

    [Serializable]
    public class DependencyNode
    {
        public string AssetPath { get; init; }
        public Vector2 Position { get; set; }
        public int Depth { get; init; }
        public bool IsRoot { get; init; }
        public List<DependencyNode> Dependencies { get; init; } = new();
        public List<DependencyNode> Dependents { get; init; } = new();
    }
    #endregion
}
