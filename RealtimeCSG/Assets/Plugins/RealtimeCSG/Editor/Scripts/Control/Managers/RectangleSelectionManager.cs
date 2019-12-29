using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using InternalRealtimeCSG;
using RealtimeCSG.Components;
using Object = UnityEngine.Object;

namespace RealtimeCSG
{
	internal static class RectSelection 
	{
		public static bool	Valid			{ get { return reflectionSucceeded; } }
		public static int	RectSelectionID { get; private set; }
		public static SceneView SceneView
		{
			get
			{
				return sceneView;
			}
			set
			{
				if (sceneView == value)
					return;
				sceneView = value;
				rectSelection = rectSelectionField.GetValue(sceneView);
			}
		}
		public static bool		RectSelecting		{ get { return (bool) rectSelectingField.GetValue(rectSelection);			} }
        public static Vector2	SelectStartPoint	{ get { return (Vector2) selectStartPointField.GetValue(rectSelection);		} }
        public static Vector2	SelectMousePoint	{ get { return (Vector2) selectMousePointField.GetValue(rectSelection);		} }
		public static Object[]	SelectionStart		{ get { return (Object[]) selectionStartField.GetValue(rectSelection);		} set { selectionStartField.SetValue(rectSelection, value); } }
        public static Object[]	CurrentSelection	{ get { return (Object[]) currentSelectionField.GetValue(rectSelection);	} set { currentSelectionField.SetValue(rectSelection, value); } }
        public static Dictionary<GameObject, bool> LastSelection { get { return (Dictionary<GameObject, bool>) lastSelectionField.GetValue(rectSelection); } }

		static object rectSelection;
		static object selectionTypeAdditive;
		static object selectionTypeSubtractive;
		static object selectionTypeNormal;

		static SceneView sceneView;

		static Type unityRectSelectionType;
		static Type unityEnumSelectionType;

		static FieldInfo rectSelectionField;
		static FieldInfo rectSelectingField;
		static FieldInfo selectStartPointField;
		static FieldInfo selectMousePointField;
		static FieldInfo selectionStartField;
		static FieldInfo lastSelectionField;
		static FieldInfo currentSelectionField;
		static FieldInfo rectSelectionIDField;

		static MethodInfo updateSelectionMethod;

		static bool reflectionSucceeded = false;

		static RectSelection()
		{
			reflectionSucceeded = false;

			var assemblies	= System.AppDomain.CurrentDomain.GetAssemblies();
			var types		= new List<System.Type>();
			foreach (var assembly in assemblies) {
				try {
					types.AddRange(assembly.GetTypes());
				} catch { }
			}

			unityRectSelectionType	= types.FirstOrDefault(t => t.FullName == "UnityEditor.RectSelection");
			if (unityRectSelectionType == null)
				return;

			unityEnumSelectionType	= types.FirstOrDefault(t => t.FullName == "UnityEditor.RectSelection+SelectionType");
			if (unityEnumSelectionType == null)
				return;

			rectSelectionField		= typeof(SceneView).GetField("m_RectSelection", BindingFlags.NonPublic | BindingFlags.Instance);
			if (rectSelectionField == null)
				return;

			rectSelectionIDField	= unityRectSelectionType.GetField("s_RectSelectionID", BindingFlags.NonPublic | BindingFlags.Static);
			if (rectSelectionIDField == null)
				return;

			RectSelectionID			= (int) rectSelectionIDField.GetValue(null);
			rectSelectingField		= unityRectSelectionType.GetField("m_RectSelecting",	BindingFlags.NonPublic | BindingFlags.Instance);
			selectStartPointField	= unityRectSelectionType.GetField("m_SelectStartPoint", BindingFlags.NonPublic | BindingFlags.Instance);
			selectionStartField		= unityRectSelectionType.GetField("m_SelectionStart",	BindingFlags.NonPublic | BindingFlags.Instance);
			lastSelectionField		= unityRectSelectionType.GetField("m_LastSelection",	BindingFlags.NonPublic | BindingFlags.Instance);
			currentSelectionField	= unityRectSelectionType.GetField("m_CurrentSelection", BindingFlags.NonPublic | BindingFlags.Instance);
			selectMousePointField	= unityRectSelectionType.GetField("m_SelectMousePoint", BindingFlags.NonPublic | BindingFlags.Instance);

			updateSelectionMethod	= unityRectSelectionType.GetMethod("UpdateSelection", BindingFlags.NonPublic | BindingFlags.Static,
																		null,
																		new Type[] {
																			typeof(UnityEngine.Object[]),
																			typeof(UnityEngine.Object[]),
																			unityEnumSelectionType,
																			typeof(bool)
																			},
																		null);
			selectionTypeAdditive		= Enum.Parse(unityEnumSelectionType, "Additive");
			selectionTypeSubtractive	= Enum.Parse(unityEnumSelectionType, "Subtractive");
			selectionTypeNormal			= Enum.Parse(unityEnumSelectionType, "Normal");

			reflectionSucceeded =	rectSelectionField			!= null &&
									selectStartPointField		!= null &&
									selectionStartField			!= null &&
									lastSelectionField			!= null &&
									currentSelectionField		!= null &&
									selectMousePointField		!= null &&
									updateSelectionMethod		!= null &&
									
									selectionTypeAdditive		!= null &&
									selectionTypeSubtractive	!= null &&
									selectionTypeNormal			!= null;
		}

		public static void UpdateSelection(Object[] existingSelection, Object[] newObjects, SelectionType type)
		{
			object selectionType;
            switch (type)
            {
                default:						selectionType = selectionTypeNormal; break;
                case SelectionType.Additive:	selectionType = selectionTypeAdditive; break;
                case SelectionType.Subtractive:	selectionType = selectionTypeSubtractive; break;
            }

			updateSelectionMethod.Invoke(null,
				new object[]
				{
					existingSelection,
					newObjects,
					selectionType,
					RectSelecting
				});
		}
	}

	internal sealed class RectangleSelectionManager
	{
		static HashSet<CSGNode> rectFoundTreeNode		= new HashSet<CSGNode>();
		static HashSet<GameObject> rectFoundGameObjects = new HashSet<GameObject>();
		static Vector2 prevStartGUIPoint;
		static Vector2 prevMouseGUIPoint;
		static Vector2 prevStartScreenPoint;
		static Vector2 prevMouseScreenPoint;
		static bool rectClickDown						= false;
		static bool mouseDragged						= false;
		static int passiveControlFrames					= 0;
		static Vector2 clickMousePosition				= Vector2.zero;

		public static SelectionType GetCurrentSelectionType()
		{
			var selectionType = SelectionType.Replace;

			// Shift only
			if (Event.current.shift && !EditorGUI.actionKey && !Event.current.alt) 
			{
				selectionType = SelectionType.Additive;

			// Action key only (Command on macOS, Control on Windows)
			} else if (!Event.current.shift && EditorGUI.actionKey && !Event.current.alt) 
			{
				selectionType = SelectionType.Subtractive;
			}
			return selectionType;
		}

		static void RemoveGeneratedMeshesFromSelection() 
		{
			var selectedObjects = Selection.objects;
			if (selectedObjects != null) {
				var foundObjects = selectedObjects;

				RemoveGeneratedMeshesFromArray(ref foundObjects);

				if (foundObjects.Length != selectedObjects.Length)
					Selection.objects = foundObjects;
			}
		}

		static bool RemoveGeneratedMeshesFromArray(ref Object[] selection)
		{
			var found = new List<Object>();
			for (int i = selection.Length - 1; i >= 0; i--) {
				var obj = selection[i];
				if (MeshInstanceManager.IsObjectGenerated(obj))
					continue;
				found.Add(obj);
			}
			if (selection.Length != found.Count)
			{
				selection = found.ToArray();
				return true;
			}
			return false;
		}

		internal static void Update(SceneView sceneView)
		{
			if (!RectSelection.Valid)
			{
				prevStartGUIPoint = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
				prevMouseGUIPoint = prevStartGUIPoint;
				prevStartScreenPoint = MathConstants.zeroVector2;
				prevMouseScreenPoint = MathConstants.zeroVector2;
				rectFoundGameObjects.Clear();
				return;
			}
			RectSelection.SceneView = sceneView;

			var evt					= Event.current;
			var rectSelectionID		= RectSelection.RectSelectionID;
			var hotControl			= GUIUtility.hotControl;
			var areRectSelecting	= hotControl == rectSelectionID;
			var typeForControl		= evt.GetTypeForControl(rectSelectionID);

			// Check if we're rect-selecting
			if (areRectSelecting)
			{
				if ((typeForControl == EventType.Used || evt.commandName == "ModifierKeysChanged") &&
					RectSelection.RectSelecting)
				{
					var selectStartPoint = RectSelection.SelectStartPoint;
					var selectMousePoint = RectSelection.SelectMousePoint;

					// Determine if our frustrum changed since last time
					bool modified = false;
					bool needUpdate = false;
					if (prevStartGUIPoint != selectStartPoint)
					{
						prevStartGUIPoint		= selectStartPoint;
                        prevStartScreenPoint	= evt.mousePosition;
                        needUpdate				= true;
					}
					if (prevMouseGUIPoint != selectMousePoint)
					{
						prevMouseGUIPoint		= selectMousePoint;
                        prevMouseScreenPoint	= evt.mousePosition;
                        needUpdate				= true;
					}
					if (needUpdate)
					{
						var rect = CameraUtility.PointsToRect(prevStartScreenPoint, prevMouseScreenPoint);
						if (rect.width > 3 && rect.height > 3)
						{
							var frustum = CameraUtility.GetCameraSubFrustumGUI(Camera.current, rect);

							// Find all brushes (and it's GameObjects) that are within the frustrum
							if (SceneQueryUtility.GetItemsInFrustum(frustum.Planes, rectFoundGameObjects))
							{
								modified = true;
							} else
							{
								if (rectFoundGameObjects != null && rectFoundGameObjects.Count > 0)
								{
									rectFoundGameObjects.Clear();
									modified = true;
								}
							}
						}

						var selectionType			= GetCurrentSelectionType();
						Object[] currentSelection	= null;
						var originalLastSelection	= RectSelection.LastSelection;
						var originalSelectionStart	= RectSelection.SelectionStart;

						if (modified && rectFoundGameObjects != null && rectFoundGameObjects.Count > 0)
						{
							foreach (var obj in rectFoundGameObjects)
							{
								// If it hasn't already been added, add the obj
								if (!originalLastSelection.ContainsKey(obj))
								{
									originalLastSelection.Add(obj, false);
								}
							}
							currentSelection = originalLastSelection.Keys.ToArray();
							RectSelection.CurrentSelection = currentSelection;

						} else if (currentSelection == null || modified)
						{
							currentSelection = originalLastSelection.Keys.ToArray();
						}

						if (RemoveGeneratedMeshesFromArray(ref originalSelectionStart))
							modified = true;

						if (currentSelection != null && RemoveGeneratedMeshesFromArray(ref currentSelection))
							modified = true;

						if ((evt.commandName == "ModifierKeysChanged" || modified))
						{
							var foundObjects = currentSelection;

							RemoveGeneratedMeshesFromArray(ref foundObjects);
							RectSelection.UpdateSelection(originalSelectionStart, foundObjects, GetCurrentSelectionType());
						}
					}
				}
			}

			if (hotControl != rectSelectionID)
			{
				prevStartGUIPoint = Vector2.zero;
				prevMouseGUIPoint = Vector2.zero;
				rectFoundGameObjects.Clear();
			}

			// Passive control frames
			if (passiveControlFrames > 0)
			{
				passiveControlFrames--;
				int controlID = GUIUtility.GetControlID(FocusType.Passive);
				GUIUtility.hotControl = controlID;
			} else {
				GUIUtility.hotControl = hotControl;
			}
			
			bool click = false;
			switch (typeForControl)
			{
				case EventType.MouseDown:
				{
					rectClickDown = (evt.button == 0 && areRectSelecting);
					clickMousePosition = evt.mousePosition;
					mouseDragged = false;
					if (rectClickDown)
					{
						if (evt.shift || evt.alt)
						{
							passiveControlFrames = 1;
						}
						click = true;
						evt.Use();
					}
					break;
				}
				case EventType.MouseUp:
				{
					passiveControlFrames = 0;
					if (!mouseDragged)
					{
						if ((HandleUtility.nearestControl != 0 || evt.button != 0) &&
							(GUIUtility.keyboardControl != 0 || evt.button != 2)) 
						{
							break;
						}
					}
					rectClickDown = false;
					break;
				}
				case EventType.MouseMove:
				{
					rectClickDown = false;
					break;
				}
				case EventType.MouseDrag:
				{
					mouseDragged = true;
					passiveControlFrames = 0;
					break;
				}
				case EventType.Used:
				{
					if (!mouseDragged)
					{
						var delta = evt.mousePosition - clickMousePosition;
						if (Mathf.Abs(delta.x) > 4 || Mathf.Abs(delta.y) > 4)
						{
							mouseDragged = true;
							passiveControlFrames = 0;
						}
					}
					if (mouseDragged || !rectClickDown || evt.button != 0 || RectSelection.RectSelecting)
					{
						rectClickDown = false;
						break;
					}
					click = true;
					evt.Use();
					break;
				}

				case EventType.ValidateCommand:
				{
					if (evt.commandName != "SelectAll")
							break;
					evt.Use();
					break; 
				}
				case EventType.ExecuteCommand:
				{
					if (evt.commandName != "SelectAll")
						break;
					
					break;
				}

				case EventType.KeyDown:
				{
					if (Keys.HandleSceneKeyDown(EditModeManager.CurrentTool, true))
					{
						evt.Use();
						HandleUtility.Repaint();
					}
					break;
				}

				case EventType.KeyUp:
				{
					if (Keys.HandleSceneKeyUp(EditModeManager.CurrentTool, true))
					{
						evt.Use();
						HandleUtility.Repaint();
					}
					break;
				}
			}

			if (click) 
			{
				// Make sure GeneratedMeshes are not part of our selection
				RemoveGeneratedMeshesFromSelection();

				DoSelectionClick(sceneView, clickMousePosition);
			}
		}

		public static void DoSelectionClick(SceneView sceneView, Vector2 mousePosition)
		{
			GameObject gameObject = null;
			SceneQueryUtility.FindClickWorldIntersection(mousePosition, out gameObject);

			// If we're a child of an operation that has a "handle as one" flag set, return that instead
			gameObject = SceneQueryUtility.FindSelectionBase(gameObject);

			var selectionType = GetCurrentSelectionType();
			var selectedObjectsOnClick = new List<int>(Selection.instanceIDs);
			var instanceID = 0;

			switch (selectionType)
			{
				case SelectionType.Additive:
					if (!gameObject) 
						break;
					instanceID = gameObject.GetInstanceID();
					selectedObjectsOnClick.Add(instanceID);
					Selection.instanceIDs = selectedObjectsOnClick.ToArray();
					break;
				case SelectionType.Subtractive:
					if (!gameObject)
						break;
					instanceID = gameObject.GetInstanceID();
					selectedObjectsOnClick.Remove(instanceID);
					Selection.instanceIDs = selectedObjectsOnClick.ToArray();
					break;
				default:
					Selection.activeGameObject = gameObject;
					break;
			}
		}
	}
}