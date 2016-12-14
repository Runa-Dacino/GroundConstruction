﻿//   GroundWorkshop.cs
//
//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2016 Allis Tauri

using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using AT_Utils;
using Experience;
using Experience.Effects;

namespace GroundConstruction
{
	public class VesselInfo : ConfigNodeObject
	{
		[Persistent] public Guid vesselID;

		public override void Save(ConfigNode node)
		{
			node.AddValue("vesselID", vesselID.ToString("N"));
			base.Save(node);
		}

		public override void Load(ConfigNode node)
		{
			base.Load(node);
			var svid = node.GetValue("vesselID");
			vesselID = string.IsNullOrEmpty(svid)? Guid.Empty : new Guid(svid);
		}
	}

	public class GroundWorkshop : PartModule
	{
		static Globals GLB { get { return Globals.Instance; } }

		public class KitInfo : VesselInfo
		{
			[Persistent] public string KitName = string.Empty;

			public ModuleConstructionKit Module { get; private set; }
			public VesselKit Kit { get { return ModuleValid? Module.kit: null; } }

			public bool Valid { get { return vesselID != Guid.Empty; } }
			public bool ModuleValid { get { return Module != null && Module.Valid; } }

			public KitInfo() {}
			public KitInfo(ModuleConstructionKit kit_module)
			{
				vesselID = kit_module.vessel.id;
				Module = kit_module;
				KitName = kit_module.KitName;
			}


			public bool Recheck()
			{
				if(ModuleValid) return true;
				FindKit();
				return ModuleValid;
			}

			public static ModuleConstructionKit GetKitFromVessel(Vessel vsl)
			{
				var kit_part = vsl.Parts.Find(p => p.Modules.Contains<ModuleConstructionKit>());
				return kit_part == null? null : kit_part.Modules.GetModule<ModuleConstructionKit>();
			}

			public ModuleConstructionKit FindKit()
			{
				var kit_vsl = FlightGlobals.FindVessel(vesselID);
				Module = kit_vsl == null? null : GetKitFromVessel(kit_vsl);
				return Module;
			}

			public override string ToString()
			{
				var s = string.Format("\"{0}\"", KitName);
				if(ModuleValid)
				{
					s += Module.kit.WorkLeft > 0? 
						string.Format(" needs {0:F1} SKH, {1}", Module.kit.WorkLeft/3600, Module.KitStatus) : 
						" Complete.";
				}
				return s;
			}
		}

		[KSPField(isPersistant = true)] public bool Working;
		[KSPField(isPersistant = true)] public double LastUpdateTime = -1;
		[KSPField(isPersistant = true)] public PersistentQueue<KitInfo> Queue = new PersistentQueue<KitInfo>();
		[KSPField(isPersistant = true)] public KitInfo KitUnderConstruction = new KitInfo();
		[KSPField(isPersistant = true)] public double ETA = -1;
		public string ETA_Display { get; private set; } = "Stalled...";

		float workers = 0;
		float distance_mod = -1;

		public override void OnAwake()
		{
			base.OnAwake();
			GameEvents.onGameStateSave.Add(onGameStateSave);
		}

		void OnDestroy()
		{
			GameEvents.onGameStateSave.Remove(onGameStateSave);
		}

		public override void OnInactive()
		{
			base.OnInactive();
			GroundConstructionScenario.DeregisterWorkshop(this);
		}

		void onGameStateSave(ConfigNode node)
		{ GroundConstructionScenario.RegisterWorkshop(this); }

		public override void OnStart(StartState state)
		{
			base.OnStart(state);
			LockName = "GroundWorkshop"+GetInstanceID();
			update_workers();
			GroundConstructionScenario.RegisterWorkshop(this);
		}

		void update_queue()
		{
			if(Queue.Count == 0) return;
			Queue = new PersistentQueue<KitInfo>(Queue.Where(kit => kit.Recheck()));
		}

		List<KitInfo> nearby_unbuilt_kits = new List<KitInfo>();
		List<KitInfo> nearby_built_kits = new List<KitInfo>();
		void update_nearby_kits()
		{
			if(!FlightGlobals.ready) return;
			var queued = new HashSet<Guid>(Queue.Select(k => k.vesselID));
			nearby_unbuilt_kits.Clear();
			nearby_built_kits.Clear();
			foreach(var vsl in FlightGlobals.Vessels)
			{
				if(!vsl.loaded) continue;
				var vsl_kit = KitInfo.GetKitFromVessel(vsl);
				if(vsl_kit != null && vsl_kit.Valid &&
				   vsl_kit != KitUnderConstruction.Module && !queued.Contains(vsl.id) &&
				   (vessel.vesselTransform.position-vsl.vesselTransform.position).magnitude < GLB.MaxDistanceToWorkshop)
				{
					if(vsl_kit.Completeness < 1)
						nearby_unbuilt_kits.Add(new KitInfo(vsl_kit));
					else nearby_built_kits.Add(new KitInfo(vsl_kit));
				}
			}
		}

		void update_workers()
		{
			workers = 0;
			foreach(var kerbal in part.protoModuleCrew)
			{
				var worker = 0;
				var trait = kerbal.experienceTrait;
				foreach(var effect in trait.Effects)
				{
					if(effect is DrillSkill ||
					   effect is ConverterSkill ||
					   effect is RepairSkill)
					{ worker = 1; break; }
				}
				worker *= trait.CrewMemberExperienceLevel();
				workers += worker;
			}
			this.Log("workers: {}", workers);//debug
		}

		bool can_construct()
		{
			if(!vessel.Landed)
			{
				Utils.Message("Cannot construct unless landed.");
				return false;
			}
			if(workers.Equals(0))
			{
				Utils.Message("No engineers in the workshop.");
				return false;
			}
			if(vessel.horizontalSrfSpeed > GLB.DeployMaxSpeed)
			{
				Utils.Message("Cannot construct while mooving.");
				return false;
			}
			if(vessel.angularVelocity.sqrMagnitude > GLB.DeployMaxAV)
			{
				Utils.Message("Cannot construct while rotating.");
				return false;
			}
			return true;
		}

		float dist2target(KitInfo kit)
		{ return (kit.Module.vessel.vesselTransform.position-vessel.vesselTransform.position).magnitude; }

		void update_distance_mod()
		{
			var dist = dist2target(KitUnderConstruction);
			if(dist > GLB.MaxDistanceToWorkshop) distance_mod = 0;
			else distance_mod = Mathf.Lerp(1, GLB.MaxDistanceEfficiency, 
			                               Mathf.Max((dist-GLB.MinDistanceToWorkshop)/GLB.MaxDistanceToWorkshop, 0));
			this.Log("dist: {}, mod {}", dist, distance_mod);//debug
		}

		void update_ETA()
		{
			update_workers();
			update_distance_mod();
			if(workers > 0 && distance_mod > 0)
			{
				ETA = KitUnderConstruction.Kit.WorkLeft;
				if(KitUnderConstruction.Kit.PartUnderConstruction != null)
					ETA -= KitUnderConstruction.Kit.PartUnderConstruction.WorkDone;
				if(ETA < 0) ETA = 0;
				ETA /= workers*distance_mod;
				ETA_Display = "Time left: "+KSPUtil.PrintTimeCompact(ETA, false);
				this.Log(ETA_Display);//debug
			}
			else 
			{
				ETA = -1;
				ETA_Display = "Stalled...";
			}
		}

		void Update()
		{
			if(Working && KitUnderConstruction.ModuleValid) 
			{
				if(can_construct()) update_ETA();
				else stop();
			}
			if(show_window)
			{
				update_queue();
				update_nearby_kits();
			}
		}

		void start()
		{
			Working = true;
			distance_mod = -1;
			LastUpdateTime = -1;
			update_ETA();
			GroundConstructionScenario.RegisterWorkshop(this);
		}

		void stop()
		{
			Working = false;
			distance_mod = -1;
			LastUpdateTime = -1;
			GroundConstructionScenario.RegisterWorkshop(this);
		}

		bool start_next_kit()
		{
			if(Queue.Count > 0)
			{
				while(Queue.Count > 0 && !KitUnderConstruction.ModuleValid)
				{
					KitUnderConstruction = Queue.Dequeue();
					KitUnderConstruction.FindKit();
				}
				if(KitUnderConstruction.ModuleValid) 
				{
					start();
					return true;
				}
			}
			KitUnderConstruction = new KitInfo();
			stop();
			return false;
		}

		double DoSomeWork(double available_work)
		{
			if(distance_mod < 0) update_distance_mod();
			if(distance_mod.Equals(0))
			{ 
				Utils.Message("{0} is too far away.", KitUnderConstruction.KitName);
				if(start_next_kit()) Utils.Message("Switching to the next kit in line.");
				return available_work; 
			}
			//try to get the structure resource
			var max_work = available_work*distance_mod;
			var work = max_work;
			double required_ec;
			var required_res = KitUnderConstruction.Module.RequiredMass(ref work, out required_ec)/GLB.StructureResource.density;
			var have_res = part.RequestResource(GLB.StructureResourceID, required_res);
			this.Log("work left {}, work {}, n.res {}, n.EC {}", KitUnderConstruction.Kit.WorkLeft, work, required_res, required_ec);//debug
			if(have_res.Equals(0)) 
			{
				Utils.Message("Not enough {0}. Construction of {0} was put on hold.", 
				              GLB.StructureResourceName, KitUnderConstruction.KitName);
				stop();
				return 0;
			}
			//try to get EC
			var have_ec = part.RequestResource(Utils.ElectricChargeID, required_ec);
			if(have_ec/required_ec < GLB.WorkshopShutdownThreshold) 
			{ 
				Utils.Message("Not enough energy. Construction of {0} was put on hold.", 
				              KitUnderConstruction.KitName);
				stop();
				return 0;
			}
			//do the work, if any
			have_res *= have_ec/required_ec;
			work *= have_res/required_res;
			this.Log("work {}, res {}/{}, EC {}/{}", work, have_res, required_res, have_ec, required_ec);//debug
			KitUnderConstruction.Module.DoSomeWork(work);
			if(KitUnderConstruction.Module.Completeness >= 1)
				start_next_kit();
			//return unused structure resource
			if(have_res < required_res)
				part.RequestResource(GLB.StructureResourceID, have_res-required_res);
			this.Log("work still available {}", available_work-work/distance_mod);
			return available_work-work/distance_mod;
		}

		void FixedUpdate()
		{
			if(!HighLogic.LoadedSceneIsFlight || !Working || workers.Equals(0)) return;
			var deltaTime = GetDeltaTime();
			this.Log("deltaTime: {}, fixedDeltaTime {}", deltaTime, TimeWarp.fixedDeltaTime);//debug
			if(deltaTime < 0) return;
			//check current kit
			if(!KitUnderConstruction.Recheck() && !start_next_kit()) return;
			var available_work = workers*deltaTime;
			while(Working && available_work > 0)
				available_work = DoSomeWork(available_work);
		}

		double GetDeltaTime()
		{
			if(Time.timeSinceLevelLoad < 1 || !FlightGlobals.ready) return -1;
			if(LastUpdateTime < 0)
			{
				LastUpdateTime = Planetarium.GetUniversalTime();
				return TimeWarp.fixedDeltaTime;
			}
			var time = Planetarium.GetUniversalTime();
			var dT = time - LastUpdateTime;
			LastUpdateTime = time;
			return dT;
		}

		#region Resource Transfer
		bool transfer_resources;
		readonly ResourceManifestList transfer_list = new ResourceManifestList();
		VesselResources host_resources, kit_resources;
		KitInfo target_kit;

		void setup_resource_transfer(KitInfo target)
		{
			if(!target.Recheck()) return;
			if(dist2target(target) > GLB.MaxDistanceToWorkshop)
			{
				Utils.Message("{0} is too far away. Needs to be closer that {1}m", 
				              target.KitName, GLB.MaxDistanceToWorkshop);
				return;
			}
			target_kit = target;
			host_resources = new VesselResources(vessel);
			kit_resources = target_kit.Module.GetConstructResources();
			transfer_list.NewTransfer(host_resources, kit_resources);
			transfer_resources = transfer_list.Count > 0;
			if(transfer_resources) return;
			host_resources = null;
			kit_resources = null;
			target_kit = null;
		}
		#endregion

		#region GUI
		readonly ResourceTransferWindow resources_window = new ResourceTransferWindow();

		[KSPField(isPersistant = true)] public bool show_window;
		const float width = 500;
		const float height = 50;

		[KSPEvent(guiName = "Construction Window", guiActive = true, active = true)]
		public void ToggleConstructionWindow()
		{ show_window = !show_window; }

		void info_pane()
		{
			if(KitUnderConstruction.ModuleValid || Queue.Count > 0)
			{
				if(Utils.ButtonSwitch("Process the Queue", ref Working, "", GUILayout.ExpandWidth(true)))
				{ Working &= can_construct(); }
			}
			if(KitUnderConstruction.ModuleValid) 
			{
				GUILayout.BeginVertical(Styles.white);
				GUILayout.Label("Constructing: "+KitUnderConstruction.KitName, Styles.green, GUILayout.ExpandWidth(true));
				GUILayout.Label(KitUnderConstruction.Module.KitStatus, Styles.fracStyle(KitUnderConstruction.Module.Completeness), GUILayout.ExpandWidth(true));
				if(KitUnderConstruction.Module.Completeness < 1)
					GUILayout.Label(KitUnderConstruction.Module.PartStatus, Styles.fracStyle(KitUnderConstruction.Module.PartCompleteness), GUILayout.ExpandWidth(true));
				if(Working)
				{
					if(distance_mod < 1)
						GUILayout.Label(string.Format("Efficiency (due to distance): {0:P1}", distance_mod), Styles.fracStyle(distance_mod), GUILayout.ExpandWidth(true));
					GUILayout.Label(ETA_Display, Styles.boxed_label, GUILayout.ExpandWidth(true));
                }
				if(GUILayout.Button("Move back to the Queue", 
				                    Styles.danger_button, GUILayout.ExpandWidth(true)))
				{
					if(Queue.Count == 0) stop();
					Queue.Enqueue(KitUnderConstruction);
					KitUnderConstruction = new KitInfo();
				}
				GUILayout.EndVertical();
			}
			else GUILayout.Label("Nothing is under construction now.", Styles.boxed_label, GUILayout.ExpandWidth(true));
		}

		Vector2 queue_scroll = Vector2.zero;
		void queue_pane()
		{
			if(Queue.Count > 0)
			{
				GUILayout.Label("Construction Queue:", GUILayout.ExpandWidth(true));
				GUILayout.BeginVertical(Styles.white);
				queue_scroll = GUILayout.BeginScrollView(queue_scroll, GUILayout.Height(height), GUILayout.Width(width));
				KitInfo del = null;
				KitInfo up = null;
				foreach(var info in Queue) 
				{
					GUILayout.BeginHorizontal();
					GUILayout.Label(info.ToString(), Styles.boxed_label, GUILayout.ExpandWidth(true));
					if(GUILayout.Button(new GUIContent("^", "Move up"), 
					                    Styles.normal_button, GUILayout.Width(25)))
						up = info;
					if(GUILayout.Button(new GUIContent("X", "Remove from Queue"), 
					                    Styles.danger_button, GUILayout.Width(25))) 
						del = info;
					GUILayout.EndHorizontal();
				}
				if(del != null) Queue.Remove(del);
				else if(up != null) Queue.MoveUp(up);
				GUILayout.EndScrollView();
				GUILayout.EndVertical();
			}
		}

		Vector2 built_scroll = Vector2.zero;
		void built_kits_pane()
		{
			if(nearby_built_kits.Count == 0) return;
			GUILayout.Label("Complete DIY kits nearby:", GUILayout.ExpandWidth(true));
			GUILayout.BeginVertical(Styles.white);
			built_scroll = GUILayout.BeginScrollView(built_scroll, GUILayout.Height(height), GUILayout.Width(width));
			KitInfo resources = null;
			KitInfo launch = null;
			foreach(var info in nearby_built_kits)
			{
				GUILayout.BeginHorizontal();
				GUILayout.Label(info.ToString(), Styles.boxed_label, GUILayout.ExpandWidth(true));
				if(GUILayout.Button(new GUIContent("Resources", "Transfer resources between the workshop and the assembled vessel"), 
				                    Styles.active_button, GUILayout.ExpandWidth(false)))
					resources = info;
				if(GUILayout.Button(new GUIContent("Launch", "Launch assembled vessel"), 
				                    Styles.danger_button, GUILayout.ExpandWidth(false)))
					launch = info;
				GUILayout.EndHorizontal();
			}
			if(resources != null) 
				setup_resource_transfer(resources);
			if(launch != null && launch.Recheck())
				launch.Module.Launch();
			GUILayout.EndScrollView();
			GUILayout.EndVertical();
		}

		Vector2 unbuilt_scroll = Vector2.zero;
		void nearby_kits_pane()
		{
			if(nearby_unbuilt_kits.Count == 0) return;
			GUILayout.Label("Uncomplete DIY kits nearby:", GUILayout.ExpandWidth(true));
			GUILayout.BeginVertical(Styles.white);
			unbuilt_scroll = GUILayout.BeginScrollView(unbuilt_scroll, GUILayout.Height(height), GUILayout.Width(width));
			KitInfo add = null;
			KitInfo deploy = null;
			foreach(var info in nearby_unbuilt_kits)
			{
				GUILayout.BeginHorizontal();
				GUILayout.Label(info.ToString(), Styles.boxed_label, GUILayout.ExpandWidth(true));
				if(info.Module.Deployed)
				{
					if(GUILayout.Button(new GUIContent("Add", "Add this kit to construction queue"), 
					                    Styles.enabled_button, GUILayout.ExpandWidth(false)))
						add = info;
				}
				else if(info.Module.Deploying)
					GUILayout.Label(info.Module.KitStatus, GUILayout.ExpandWidth(false));
				else
				{
					if(GUILayout.Button(new GUIContent("Deploy", "Deploy this kit and fix it to the ground"), 
					                    Styles.active_button, GUILayout.ExpandWidth(false)))
						deploy = info;
				}
				GUILayout.EndHorizontal();
			}
			if(add != null) 
				Queue.Enqueue(add);
			else if(deploy != null && deploy.Recheck())
				deploy.Module.Deploy();
			GUILayout.EndScrollView();
			GUILayout.EndVertical();
		}

		Rect WindowPos = new Rect((Screen.width-width)/2, Screen.height/4, width, height*4);
		void main_window(int WindowID)
		{
			GUILayout.BeginVertical();
			nearby_kits_pane();
			queue_pane();
			info_pane();
			built_kits_pane();
			if(GUILayout.Button("Close", Styles.close_button, GUILayout.ExpandWidth(true)))
				show_window = false;
			GUILayout.EndVertical();
			GUIWindowBase.TooltipsAndDragWindow(WindowPos);
		}

		string LockName = ""; //inited OnStart
		void OnGUI()
		{
			if(Event.current.type != EventType.Layout && Event.current.type != EventType.Repaint) return;
			if(show_window && GUIWindowBase.HUD_enabled && vessel.isActiveVessel)
			{
				Styles.Init();
				Utils.LockIfMouseOver(LockName, WindowPos);
				WindowPos = GUILayout.Window(GetInstanceID(), 
				                             WindowPos, main_window, part.partInfo.title,
				                             GUILayout.Width(width),
				                             GUILayout.Height(height)).clampToScreen();
				if(transfer_resources)
				{
					resources_window.Draw(string.Format("Transfer resources to {0}", target_kit.KitName), transfer_list);
					if(resources_window.transferNow && target_kit.Recheck())
					{
						double dM, dC;
						transfer_list.TransferResources(host_resources, kit_resources, out dM, out dC);
						target_kit.Kit.Mass += (float)dM;
						target_kit.Kit.Cost += (float)dC;
					}
					if(resources_window.Closed)
					{
						resources_window.UnlockControls();
						transfer_resources = false;
					}
				}
			}
			else
			{
				Utils.LockIfMouseOver(LockName, WindowPos, false);
				resources_window.UnlockControls();
			}
		}
		#endregion
	}
}
