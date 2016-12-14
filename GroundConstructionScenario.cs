﻿//   GroundConstructionScenario.cs
//
//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2016 Allis Tauri

using System;
using System.Collections.Generic;
using UnityEngine;
using AT_Utils;

namespace GroundConstruction
{
	[KSPScenario(ScenarioCreationOptions.AddToAllGames, new []
	{
		GameScenes.SPACECENTER,
		GameScenes.FLIGHT,
		GameScenes.TRACKSTATION,
	})]
	public class GroundConstructionScenario : ScenarioModule
	{
		static Globals GLB { get { return Globals.Instance; } }

		public class WorkshopInfo : VesselInfo
		{
			public enum Status { IDLE, ACTIVE, COMPLETE };

			[Persistent] public uint   id;
			[Persistent] public string CB;
			[Persistent] public string Name;
			[Persistent] public string KitName;
			[Persistent] public Status State;
			[Persistent] public double EndUT;
			[Persistent] public string ETA;

			public WorkshopInfo() {}
			public WorkshopInfo(GroundWorkshop workshop) 
			{
				vesselID = workshop.vessel.id;
				id = workshop.part.flightID;
				CB = workshop.vessel.mainBody.bodyName;
				Name = workshop.vessel.vesselName;
				State = Status.IDLE;
				EndUT = -1;
				if(workshop.KitUnderConstruction.Valid) 
				{
					State = Status.ACTIVE;
					KitName = workshop.KitUnderConstruction.KitName;
					if(workshop.ETA > 0) EndUT = Planetarium.GetUniversalTime()+workshop.ETA;
				}
			}

			public bool SwitchTo()
			{
				var vsl = FlightGlobals.FindVessel(vesselID);
				if(vsl == null) 
				{
					Utils.Message("{0} was not found in the game", Name);
					return false;
				}
				if(FlightGlobals.ready) FlightGlobals.SetActiveVessel(vsl);
				else
				{
					GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
					FlightDriver.StartAndFocusVessel("persistent", FlightGlobals.Vessels.IndexOf(vsl));
				}
				return true;
			}

			public GUIStyle GetStyle()
			{
				if(State == Status.COMPLETE) 
					return Styles.green;
				if(State == Status.ACTIVE)
					return EndUT > 0? Styles.yellow : Styles.red;
				return Styles.white;
			}

			public override string ToString()
			{ 
				if(State == Status.COMPLETE)
					return string.Format("[{0}] \"{1}\" assembled \"{2}\".", CB, Name, KitName);
				if(State == Status.ACTIVE)
					return string.Format("[{0}] \"{1}\" is building \"{2}\". {3}", CB, Name, KitName, ETA);
				return string.Format("[{0}] \"{1}\" is idle.", CB, Name);
			}
		}

		static SortedDictionary<uint,WorkshopInfo> Workshops = new SortedDictionary<uint,WorkshopInfo>();

		public static void RegisterWorkshop(GroundWorkshop workshop)
		{
			Workshops[workshop.part.flightID] = new WorkshopInfo(workshop);
			Utils.Log("Workshop registered: {} [{}], {}, ETA: {}", 
			          workshop.vessel.vesselName, workshop.vessel.id, 
			          workshop.KitUnderConstruction.KitName, workshop.ETA); //debug
		}

		public static bool DeregisterWorkshop(GroundWorkshop workshop)
		{ return Workshops.Remove(workshop.part.flightID); }

		IEnumerator<YieldInstruction> slow_update()
		{
			while(true)
			{
				var now = Planetarium.GetUniversalTime();
				var finished = false;
				foreach(var workshop in Workshops.Values)
				{
					if(workshop.State == WorkshopInfo.Status.IDLE) continue;
					if(workshop.EndUT > 0 && 
					   workshop.EndUT < now)
					{
						Utils.Message(10, "Engineers at '{0}' should have assembled the '{1}' by now.",
						              workshop.Name, workshop.KitName);
						workshop.State = WorkshopInfo.Status.IDLE;
						finished = true;
					}
					else
					{
						workshop.ETA = workshop.EndUT > 0?
							"Time left: "+KSPUtil.PrintTimeCompact(workshop.EndUT-now, false) : "Stalled...";
					}
				}
				if(finished) TimeWarp.SetRate(0, false);
				yield return new WaitForSeconds(1);
			}
		}

		public override void OnAwake()
		{
			base.OnAwake();
			StartCoroutine(slow_update());
		}

		public override void OnSave(ConfigNode node)
		{
			base.OnSave(node);
			var workshops = new PersistentList<WorkshopInfo>(Workshops.Values);
			workshops.Sort((a,b) => a.id.CompareTo(b.id));
			workshops.Save(node.AddNode("Workshops"));
		}

		public override void OnLoad(ConfigNode node)
		{
			base.OnLoad(node);
			Workshops.Clear();
			var wnode = node.GetNode("Workshops");
			if(wnode != null)
			{
				var workshops = new PersistentList<WorkshopInfo>();
				workshops.Load(wnode);
				workshops.ForEach(w => Workshops.Add(w.id, w));
			}
		}

		#region GUI
		const float width = 500;
		const float height = 50;

		static bool show_window;
		public static void ShowWindow(bool show) { show_window = show; }
		public static void ToggleWindow() { show_window = !show_window; }

		Vector2 workshops_scroll = Vector2.zero;
		Rect WindowPos = new Rect(Screen.width-width-100, 0, Screen.width/4, Screen.height/4);
		void main_window(int WindowID)
		{
			GUILayout.BeginVertical(Styles.white);
			if(Workshops.Count > 0)
			{
				workshops_scroll = GUILayout.BeginScrollView(workshops_scroll, GUILayout.Height(height), GUILayout.Width(width));
				WorkshopInfo switchto = null;
				foreach(var item in Workshops) 
				{
					var info = item.Value;
					GUILayout.BeginHorizontal();
					GUILayout.Label(info.ToString(), info.GetStyle(), GUILayout.ExpandWidth(true));
					if(GUILayout.Button(new GUIContent("Focus", "Switch to this workshop"), 
					                    Styles.active_button, GUILayout.ExpandWidth(false)))
						switchto = info;
					GUILayout.EndHorizontal();
				}
				if(switchto != null) 
				{
					if(!switchto.SwitchTo()) 
						Workshops.Remove(switchto.id);
				}
				GUILayout.EndScrollView();
				if(GUILayout.Button("Close", Styles.close_button, GUILayout.ExpandWidth(true)))
					show_window = false;
			}
			else GUILayout.Label("No Ground Workshops", Styles.white, GUILayout.ExpandWidth(true));
			GUILayout.EndVertical();
			GUIWindowBase.TooltipsAndDragWindow(WindowPos);
		}

		const string LockName = "GroundConstructionScenario";
		void OnGUI()
		{
			if(Event.current.type != EventType.Layout && Event.current.type != EventType.Repaint) return;
			if(show_window && GUIWindowBase.HUD_enabled)
			{
				Styles.Init();

					Utils.LockIfMouseOver(LockName, WindowPos);
					WindowPos = GUILayout.Window(GetInstanceID(), 
				                                 WindowPos, main_window, "Ground Workshops",
					                             GUILayout.Width(width),
					                             GUILayout.Height(height)).clampToScreen();
			}
			else Utils.LockIfMouseOver(LockName, WindowPos, false);
		}
		#endregion
	}
}
