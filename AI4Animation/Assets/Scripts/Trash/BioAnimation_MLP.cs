﻿using UnityEngine;
using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class BioAnimation_MLP : MonoBehaviour {

	public bool Inspect = false;
	public bool ShowTrajectory = true;
	public bool ShowVelocities = true;

	public float TargetBlending = 0.25f;
	public float StyleTransition = 0.25f;
	public bool TrajectoryControl = true;
	public float TrajectoryCorrection = 1f;

	public Transform Root;
	public Transform[] Joints = new Transform[0];

	public Controller Controller;
	public Character Character;
	public MLP MLP;

	public bool SolveIK = true;
	public FootIK[] IKSolvers = new FootIK[0];

	private Trajectory Trajectory;

	private Vector3 TargetDirection;
	private Vector3 TargetVelocity;
	private float Bias;

	private Vector3[] Positions = new Vector3[0];
	private Vector3[] Forwards = new Vector3[0];
	private Vector3[] Ups = new Vector3[0];
	private Vector3[] Velocities = new Vector3[0];
	
	//Trajectory for 60 Hz framerate
	private const int PointSamples = 12;
	private const int RootPointIndex = 60;
	private const int PointDensity = 10;

	private int TrajectoryDimIn;
	private int TrajectoryDimOut;
	private int JointDimIn;
	private int JointDimOut;

	void Reset() {
		Root = transform;
		Controller = new Controller();
		Character = new Character();
		Character.BuildHierarchy(transform);
		MLP = new MLP();
	}

	void Awake() {
		TargetDirection = new Vector3(Root.forward.x, 0f, Root.forward.z);
		TargetVelocity = Vector3.zero;
		Positions = new Vector3[Joints.Length];
		Forwards = new Vector3[Joints.Length];
		Ups = new Vector3[Joints.Length];
		Velocities = new Vector3[Joints.Length];
		Trajectory = new Trajectory(111, Controller.Styles.Length, Root.position, TargetDirection);
		Trajectory.Postprocess();
		for(int i=0; i<Joints.Length; i++) {
			Positions[i] = Joints[i].position;
			Forwards[i] = Joints[i].forward;
			Ups[i] = Joints[i].up;
			Velocities[i] = Vector3.zero;
		}
		if(MLP.Parameters == null) {
			Debug.Log("No parameters loaded.");
			return;
		}
		MLP.Initialise();
	}

	void Start() {
		Utility.SetFPS(60);
		JointDimIn = 12;
		JointDimOut = 12;
		TrajectoryDimIn = 7 + Controller.Styles.Length;
		TrajectoryDimOut = 4;
	}

	public Trajectory GetTrajectory() {
		return Trajectory;
	}

	public void UseIK(bool value) {
		SolveIK = value;
		if(SolveIK) {
			for(int i=0; i<IKSolvers.Length; i++) {
				IKSolvers[i].Goal = IKSolvers[i].GetTipPosition();
			}
		}
	}

	public void AutoDetect() {
		SetJointCount(0);
		Action<Transform> recursion = null;
		recursion = new Action<Transform>((transform) => {
			if(Character.FindSegment(transform.name) != null) {
				AddJoint(transform);
			}
			for(int i=0; i<transform.childCount; i++) {
				recursion(transform.GetChild(i));
			}
		});
		recursion(Root);
	}

	public void SetTargetDirection(Vector3 direction) {
		TargetDirection = direction;
	}

	public void SetTargetVelocity(Vector3 velocity) {
		TargetVelocity = velocity;
	}

	void Update() {	
		if(TrajectoryControl) {
			//Update Target Direction / Velocity 
			TargetDirection = Vector3.Lerp(TargetDirection, Quaternion.AngleAxis(Controller.QueryTurn()*60f, Vector3.up) * Trajectory.Points[RootPointIndex].GetDirection(), TargetBlending);
			TargetVelocity = Vector3.Lerp(TargetVelocity, (Quaternion.LookRotation(TargetDirection, Vector3.up) * Controller.QueryMove()).normalized, TargetBlending);

			//Update Bias
			Bias = Utility.Interpolate(Bias, PoolBias(), TargetBlending);

			//Update Trajectory Correction
			TrajectoryCorrection = Utility.Interpolate(TrajectoryCorrection, Mathf.Max(Controller.QueryMove().normalized.magnitude, Mathf.Abs(Controller.QueryTurn())), TargetBlending);

			//Update Style
			for(int i=0; i<Controller.Styles.Length; i++) {
				if(i==0) {
					if(!Controller.QueryAny()) {
						Trajectory.Points[RootPointIndex].Styles[i] = Utility.Interpolate(Trajectory.Points[RootPointIndex].Styles[i], 1f, StyleTransition);
					} else {
						Trajectory.Points[RootPointIndex].Styles[i] = Utility.Interpolate(Trajectory.Points[RootPointIndex].Styles[i], Controller.Styles[i].Query() ? 1f : 0f, StyleTransition);
					}
				} else {
					Trajectory.Points[RootPointIndex].Styles[i] = Utility.Interpolate(Trajectory.Points[RootPointIndex].Styles[i], Controller.Styles[i].Query() ? 1f : 0f, StyleTransition);
				}
			}

			//Predict Future Trajectory
			Vector3[] trajectory_positions_blend = new Vector3[Trajectory.Points.Length];
			trajectory_positions_blend[RootPointIndex] = Trajectory.Points[RootPointIndex].GetTransformation().GetPosition();
			for(int i=RootPointIndex+1; i<Trajectory.Points.Length; i++) {
				float bias_pos = 0.75f;
				float bias_dir = 1.25f;
				float scale_pos = (1.0f - Mathf.Pow(1.0f - ((float)(i - RootPointIndex) / (RootPointIndex)), bias_pos));
				float scale_dir = (1.0f - Mathf.Pow(1.0f - ((float)(i - RootPointIndex) / (RootPointIndex)), bias_dir));
				
				float scale = 1f / (Trajectory.Points.Length - (RootPointIndex + 1f));

				trajectory_positions_blend[i] = trajectory_positions_blend[i-1] + Vector3.Lerp(
					Trajectory.Points[i].GetPosition() - Trajectory.Points[i-1].GetPosition(), 
					scale * Bias * TargetVelocity,
					scale_pos);

				Trajectory.Points[i].SetDirection(Vector3.Lerp(Trajectory.Points[i].GetDirection(), TargetDirection, scale_dir));
				
				for(int j=0; j<Trajectory.Points[i].Styles.Length; j++) {
					Trajectory.Points[i].Styles[j] = Trajectory.Points[RootPointIndex].Styles[j];
				}
			}
			for(int i=RootPointIndex+1; i<Trajectory.Points.Length; i++) {
				Trajectory.Points[i].SetPosition(trajectory_positions_blend[i]);
			}

			for(int i=RootPointIndex; i<Trajectory.Points.Length; i+=PointDensity) {
				Trajectory.Points[i].Postprocess();
			}
			for(int i=RootPointIndex+1; i<Trajectory.Points.Length; i++) {
				Trajectory.Point prev = GetPreviousSample(i);
				Trajectory.Point next = GetNextSample(i);
				float factor = (float)(i % PointDensity) / PointDensity;

				Trajectory.Points[i].SetPosition(((1f-factor)*prev.GetPosition() + factor*next.GetPosition()));
				Trajectory.Points[i].SetDirection(((1f-factor)*prev.GetDirection() + factor*next.GetDirection()));
				Trajectory.Points[i].SetLeftsample((1f-factor)*prev.GetLeftSample() + factor*next.GetLeftSample());
				Trajectory.Points[i].SetRightSample((1f-factor)*prev.GetRightSample() + factor*next.GetRightSample());
				Trajectory.Points[i].SetSlope((1f-factor)*prev.GetSlope() + factor*next.GetSlope());
			}
		}

		if(MLP.Parameters != null) {
			//Calculate Root
			Matrix4x4 currentRoot = Trajectory.Points[RootPointIndex].GetTransformation();
			//Fix for flat terrain
			Transformations.SetPosition(
				ref currentRoot,
				new Vector3(currentRoot.GetPosition().x, 0f, currentRoot.GetPosition().z)
			);
			//

			int start = 0;
			//Input Trajectory Positions / Directions / Heights / Styles
			for(int i=0; i<PointSamples; i++) {
				Vector3 pos = GetSample(i).GetPosition().GetRelativePositionTo(currentRoot);
				Vector3 dir = GetSample(i).GetDirection().GetRelativeDirectionTo(currentRoot);
				MLP.SetInput(start + i*TrajectoryDimIn + 0, pos.x);
				MLP.SetInput(start + i*TrajectoryDimIn + 1, 0f);
				MLP.SetInput(start + i*TrajectoryDimIn + 2, pos.z);
				MLP.SetInput(start + i*TrajectoryDimIn + 3, dir.x);
				MLP.SetInput(start + i*TrajectoryDimIn + 4, dir.z);
				MLP.SetInput(start + i*TrajectoryDimIn + 5, 0f);
				MLP.SetInput(start + i*TrajectoryDimIn + 6, 0f);
				for(int j=0; j<GetSample(i).Styles.Length; j++) {
					MLP.SetInput(start + i*TrajectoryDimIn + 7 + j, GetSample(i).Styles[j]);
				}
			}
			start += TrajectoryDimIn*PointSamples;

			//Input Previous Bone Positions / Velocities
			Matrix4x4 previousRoot = Trajectory.Points[RootPointIndex-1].GetTransformation();
			//Fix for flat terrain
			Transformations.SetPosition(
				ref previousRoot,
				new Vector3(previousRoot.GetPosition().x, 0f, previousRoot.GetPosition().z)
			);
			//
			for(int i=0; i<Joints.Length; i++) {
				Vector3 pos = Positions[i].GetRelativePositionTo(previousRoot);
				Vector3 forward = Forwards[i].GetRelativeDirectionTo(previousRoot);
				Vector3 up = Ups[i].GetRelativeDirectionTo(previousRoot);
				Vector3 vel = Velocities[i].GetRelativeDirectionTo(previousRoot);
				MLP.SetInput(start + i*JointDimIn + 0, pos.x);
				MLP.SetInput(start + i*JointDimIn + 1, pos.y);
				MLP.SetInput(start + i*JointDimIn + 2, pos.z);
				MLP.SetInput(start + i*JointDimIn + 3, forward.x);
				MLP.SetInput(start + i*JointDimIn + 4, forward.y);
				MLP.SetInput(start + i*JointDimIn + 5, forward.z);
				MLP.SetInput(start + i*JointDimIn + 6, up.x);
				MLP.SetInput(start + i*JointDimIn + 7, up.y);
				MLP.SetInput(start + i*JointDimIn + 8, up.z);
				MLP.SetInput(start + i*JointDimIn + 9, vel.x);
				MLP.SetInput(start + i*JointDimIn + 10, vel.y);
				MLP.SetInput(start + i*JointDimIn + 11, vel.z);
			}
			start += JointDimIn*Joints.Length;
			
			//Predict
			MLP.Predict();

			//Update Past Trajectory
			for(int i=0; i<RootPointIndex; i++) {
				Trajectory.Points[i].SetPosition(Trajectory.Points[i+1].GetPosition());
				Trajectory.Points[i].SetDirection(Trajectory.Points[i+1].GetDirection());
				Trajectory.Points[i].SetLeftsample(Trajectory.Points[i+1].GetLeftSample());
				Trajectory.Points[i].SetRightSample(Trajectory.Points[i+1].GetRightSample());
				Trajectory.Points[i].SetSlope(Trajectory.Points[i+1].GetSlope());
				for(int j=0; j<Trajectory.Points[i].Styles.Length; j++) {
					Trajectory.Points[i].Styles[j] = Trajectory.Points[i+1].Styles[j];
				}
			}

			//Update Current Trajectory
			int end = TrajectoryDimOut*6 + JointDimOut*Joints.Length;
			Vector3 translationalOffset = new Vector3(MLP.GetOutput(end+0), 0f, MLP.GetOutput(end+1));
			float angularOffset = MLP.GetOutput(end+2);

			translationalOffset *= Utility.Exponential01(translationalOffset.magnitude / 0.001f);
			angularOffset *= Utility.Exponential01(Mathf.Abs(angularOffset) / 0.01f);

			Trajectory.Points[RootPointIndex].SetPosition(translationalOffset.GetRelativePositionFrom(currentRoot));
			Trajectory.Points[RootPointIndex].SetDirection(Quaternion.AngleAxis(angularOffset, Vector3.up) * Trajectory.Points[RootPointIndex].GetDirection());
			Trajectory.Points[RootPointIndex].Postprocess();
			Matrix4x4 nextRoot = Trajectory.Points[RootPointIndex].GetTransformation();
			//Fix for flat terrain
			Transformations.SetPosition(
				ref nextRoot,
				new Vector3(nextRoot.GetPosition().x, 0f, nextRoot.GetPosition().z)
			);
			//

			//Update Future Trajectory
			for(int i=RootPointIndex+1; i<Trajectory.Points.Length; i++) {
				Trajectory.Points[i].SetPosition(Trajectory.Points[i].GetPosition() + translationalOffset.GetRelativeDirectionFrom(nextRoot));
			}
			start = 0;
			for(int i=RootPointIndex+1; i<Trajectory.Points.Length; i++) {
				//ROOT	1		2		3		4		5
				//.x....x.......x.......x.......x.......x
				int index = i;
				int prevSampleIndex = GetPreviousSample(index).GetIndex() / PointDensity;
				int nextSampleIndex = GetNextSample(index).GetIndex() / PointDensity;
				float factor = (float)(i % PointDensity) / PointDensity;

				float prevPosX = MLP.GetOutput(start + (prevSampleIndex-6)*TrajectoryDimOut + 0);
				float prevPosZ = MLP.GetOutput(start + (prevSampleIndex-6)*TrajectoryDimOut + 1);
				float prevDirX = MLP.GetOutput(start + (prevSampleIndex-6)*TrajectoryDimOut + 2);
				float prevDirZ = MLP.GetOutput(start + (prevSampleIndex-6)*TrajectoryDimOut + 3);

				float nextPosX = MLP.GetOutput(start + (nextSampleIndex-6)*TrajectoryDimOut + 0);
				float nextPosZ = MLP.GetOutput(start + (nextSampleIndex-6)*TrajectoryDimOut + 1);
				float nextDirX = MLP.GetOutput(start + (nextSampleIndex-6)*TrajectoryDimOut + 2);
				float nextDirZ = MLP.GetOutput(start + (nextSampleIndex-6)*TrajectoryDimOut + 3);

				float posX = (1f - factor) * prevPosX + factor * nextPosX;
				float posZ = (1f - factor) * prevPosZ + factor * nextPosZ;
				float dirX = (1f - factor) * prevDirX + factor * nextDirX;
				float dirZ = (1f - factor) * prevDirZ + factor * nextDirZ;

				Trajectory.Points[i].SetPosition(
					Utility.Interpolate(
						Trajectory.Points[i].GetPosition(),
						new Vector3(posX, 0f, posZ).GetRelativePositionFrom(nextRoot),
						TrajectoryCorrection
						)
					);
				Trajectory.Points[i].SetDirection(
					Utility.Interpolate(
						Trajectory.Points[i].GetDirection(),
						new Vector3(dirX, 0f, dirZ).normalized.GetRelativeDirectionFrom(nextRoot),
						TrajectoryCorrection
						)
					);
			}
			start += TrajectoryDimOut*6;
			for(int i=RootPointIndex+PointDensity; i<Trajectory.Points.Length; i+=PointDensity) {
				Trajectory.Points[i].Postprocess();
			}
			for(int i=RootPointIndex+1; i<Trajectory.Points.Length; i++) {
				Trajectory.Point prev = GetPreviousSample(i);
				Trajectory.Point next = GetNextSample(i);
				float factor = (float)(i % PointDensity) / PointDensity;

				Trajectory.Points[i].SetPosition(((1f-factor)*prev.GetPosition() + factor*next.GetPosition()));
				Trajectory.Points[i].SetDirection(((1f-factor)*prev.GetDirection() + factor*next.GetDirection()));
				Trajectory.Points[i].SetLeftsample((1f-factor)*prev.GetLeftSample() + factor*next.GetLeftSample());
				Trajectory.Points[i].SetRightSample((1f-factor)*prev.GetRightSample() + factor*next.GetRightSample());
				Trajectory.Points[i].SetSlope((1f-factor)*prev.GetSlope() + factor*next.GetSlope());
			}

			//Compute Posture
			for(int i=0; i<Joints.Length; i++) {
				Vector3 position = new Vector3(MLP.GetOutput(start + i*JointDimOut + 0), MLP.GetOutput(start + i*JointDimOut + 1), MLP.GetOutput(start + i*JointDimOut + 2));
				Vector3 forward = new Vector3(MLP.GetOutput(start + i*JointDimOut + 3), MLP.GetOutput(start + i*JointDimOut + 4), MLP.GetOutput(start + i*JointDimOut + 5)).normalized;
				Vector3 up = new Vector3(MLP.GetOutput(start + i*JointDimOut + 6), MLP.GetOutput(start + i*JointDimOut + 7), MLP.GetOutput(start + i*JointDimOut + 8)).normalized;
				Vector3 velocity = new Vector3(MLP.GetOutput(start + i*JointDimOut + 9), MLP.GetOutput(start + i*JointDimOut + 10), MLP.GetOutput(start + i*JointDimOut + 11));
				
				Positions[i] = Vector3.Lerp(Positions[i].GetRelativePositionTo(currentRoot) + velocity, position, 0.5f).GetRelativePositionFrom(currentRoot);
				Forwards[i] = forward.GetRelativeDirectionFrom(currentRoot);
				Ups[i] = up.GetRelativeDirectionFrom(currentRoot);
				Velocities[i] = velocity.GetRelativeDirectionFrom(currentRoot);
			}
			start += JointDimOut*Joints.Length;
			
			//Update Posture
			Root.position = nextRoot.GetPosition();
			Root.rotation = nextRoot.GetRotation();
			for(int i=0; i<Joints.Length; i++) {
				Joints[i].position = Positions[i];
				Joints[i].rotation = Quaternion.LookRotation(Forwards[i], Ups[i]);
			}
			
			transform.position = new Vector3(Root.position.x, 0f, Root.position.z); //Fix for flat ground

			if(SolveIK) {
				//Step #1
				for(int i=0; i<IKSolvers.Length; i++) {
					if(IKSolvers[i].name != "Tail") {
						float heightThreshold = i==0 || i==1 ? 0.025f : 0.05f;
						float velocityThreshold = i==0 || i== 1 ? 0.015f : 0.015f;
						Vector3 goal = IKSolvers[i].GetTipPosition();
						IKSolvers[i].Goal.y = goal.y;
						float velocityDelta = (goal - IKSolvers[i].Goal).magnitude;
						float velocityWeight = Utility.Exponential01(velocityDelta / velocityThreshold);
						float heightDelta = goal.y;
						float heightWeight = Utility.Exponential01(heightDelta / heightThreshold);
						float weight = Mathf.Min(velocityWeight, heightWeight);
						IKSolvers[i].Goal = Vector3.Lerp(IKSolvers[i].Goal, goal, weight);
					}
				}
				for(int i=0; i<IKSolvers.Length; i++) {
					if(IKSolvers[i].name != "Tail") {
						IKSolvers[i].ProcessIK();
					}
				}
				for(int i=0; i<Joints.Length; i++) {
					Positions[i] = Joints[i].position;
				}
			}

			transform.position = Trajectory.Points[RootPointIndex].GetPosition(); //Fix for flat ground
			
			if(SolveIK) {
				//Step #2
				for(int i=0; i<IKSolvers.Length; i++) {
					IKSolvers[i].Goal = IKSolvers[i].GetTipPosition();
					float height = Utility.GetHeight(IKSolvers[i].Goal, LayerMask.GetMask("Ground"));
					if(IKSolvers[i].name == "Tail") {
						IKSolvers[i].Goal.y = Mathf.Max(height, height + (IKSolvers[i].Goal.y - transform.position.y));
					} else {
						IKSolvers[i].Goal.y = height + (IKSolvers[i].Goal.y - transform.position.y);
					}
				}
				Transform spine = Array.Find(Joints, x => x.name == "Spine1");
				Transform neck = Array.Find(Joints, x => x.name == "Neck");
				Transform leftShoulder = Array.Find(Joints, x => x.name == "LeftShoulder");
				Transform rightShoulder = Array.Find(Joints, x => x.name == "RightShoulder");
				Vector3 spinePosition = spine.position;
				Vector3 neckPosition = neck.position;
				Vector3 leftShoulderPosition = leftShoulder.position;
				Vector3 rightShoulderPosition = rightShoulder.position;
				float spineHeight = Utility.GetHeight(spine.position, LayerMask.GetMask("Ground"));
				float neckHeight = Utility.GetHeight(neck.position, LayerMask.GetMask("Ground"));
				float leftShoulderHeight = Utility.GetHeight(leftShoulder.position, LayerMask.GetMask("Ground"));
				float rightShoulderHeight = Utility.GetHeight(rightShoulder.position, LayerMask.GetMask("Ground"));
				spine.rotation = Quaternion.Slerp(spine.rotation, Quaternion.FromToRotation(neckPosition - spinePosition, new Vector3(neckPosition.x, neckHeight + (neckPosition.y - Root.position.y), neckPosition.z) - spinePosition) * spine.rotation, 0.5f);
				spine.position = new Vector3(spinePosition.x, spineHeight + (spinePosition.y - Root.position.y), spinePosition.z);
				neck.position = new Vector3(neckPosition.x, neckHeight + (neckPosition.y - Root.position.y), neckPosition.z);
				leftShoulder.position = new Vector3(leftShoulderPosition.x, leftShoulderHeight + (leftShoulderPosition.y - Root.position.y), leftShoulderPosition.z);
				rightShoulder.position = new Vector3(rightShoulderPosition.x, rightShoulderHeight + (rightShoulderPosition.y - Root.position.y), rightShoulderPosition.z);
				for(int i=0; i<IKSolvers.Length; i++) {
					IKSolvers[i].ProcessIK();
				}
			}

			//Update Skeleton
			Character.FetchTransformations(Root);
		}
	}

	private float PoolBias() {
		float[] styles = Trajectory.Points[RootPointIndex].Styles;
		float bias = 0f;
		for(int i=0; i<styles.Length; i++) {
			float _bias = Controller.Styles[i].Bias;
			float max = 0f;
			for(int j=0; j<Controller.Styles[i].Multipliers.Length; j++) {
				if(Input.GetKey(Controller.Styles[i].Multipliers[j].Key)) {
					max = Mathf.Max(max, Controller.Styles[i].Bias * Controller.Styles[i].Multipliers[j].Value);
				}
			}
			for(int j=0; j<Controller.Styles[i].Multipliers.Length; j++) {
				if(Input.GetKey(Controller.Styles[i].Multipliers[j].Key)) {
					_bias = Mathf.Min(max, _bias * Controller.Styles[i].Multipliers[j].Value);
				}
			}
			bias += styles[i] * _bias;
		}
		return bias;
	}
	
	private int GetJointIndex(string name) {
		return System.Array.FindIndex(Joints, x => x.name == name);
	}

	private Trajectory.Point GetSample(int index) {
		return Trajectory.Points[Mathf.Clamp(index*10, 0, Trajectory.Points.Length-1)];
	}

	private Trajectory.Point GetPreviousSample(int index) {
		return GetSample(index / 10);
	}

	private Trajectory.Point GetNextSample(int index) {
		if(index % 10 == 0) {
			return GetSample(index / 10);
		} else {
			return GetSample(index / 10 + 1);
		}
	}

	public void AddJoint(Transform joint) {
		System.Array.Resize(ref Joints, Joints.Length+1);
		Joints[Joints.Length-1] = joint;
	}

	public void SetJoint(int index, Transform t) {
		if(index < 0 || index >= Joints.Length) {
			return;
		}
		Joints[index] = t;
	}

	public void SetJointCount(int count) {
		count = Mathf.Max(0, count);
		if(Joints.Length != count) {
			System.Array.Resize(ref Joints, count);
		}
	}

	void OnRenderObject() {
		if(Root == null) {
			Root = transform;
		}

		if(ShowTrajectory) {
			if(Application.isPlaying) {
				UltiDraw.Begin();
				UltiDraw.DrawLine(Trajectory.Points[RootPointIndex].GetPosition(), Trajectory.Points[RootPointIndex].GetPosition() + TargetDirection, 0.05f, 0f, UltiDraw.Red.Transparent(0.75f));
				UltiDraw.DrawLine(Trajectory.Points[RootPointIndex].GetPosition(), Trajectory.Points[RootPointIndex].GetPosition() + TargetVelocity, 0.05f, 0f, UltiDraw.Green.Transparent(0.75f));
				UltiDraw.End();
				Trajectory.Draw(10);
			}
		}
		
		if(!Application.isPlaying) {
			Character.FetchTransformations(Root);
		}
		Character.Draw();

		if(ShowVelocities) {
			if(Application.isPlaying) {
				UltiDraw.Begin();
				for(int i=0; i<Joints.Length; i++) {
					Character.Segment segment = Character.FindSegment(Joints[i].name);
					if(segment != null) {
						UltiDraw.DrawArrow(
							Joints[i].position,
							Joints[i].position + 60f * Velocities[i],
							0.75f,
							0.0075f,
							0.05f,
							UltiDraw.Purple.Transparent(0.5f)
						);
					}
				}
				UltiDraw.End();
			}
		}
	}

	void OnDrawGizmos() {
		if(!Application.isPlaying) {
			OnRenderObject();
		}
	}

	#if UNITY_EDITOR
	[CustomEditor(typeof(BioAnimation_MLP))]
	public class BioAnimation_MLP_Editor : Editor {

		public BioAnimation_MLP Target;

		void Awake() {
			Target = (BioAnimation_MLP)target;
		}

		public override void OnInspectorGUI() {
			Undo.RecordObject(Target, Target.name);

			Inspector();
			Target.Controller.Inspector();
			Target.Character.Inspector(Target.Root);
			Target.MLP.Inspector();

			if(GUI.changed) {
				EditorUtility.SetDirty(Target);
			}
		}

		private void Inspector() {			
			Utility.SetGUIColor(UltiDraw.Grey);
			using(new EditorGUILayout.VerticalScope ("Box")) {
				Utility.ResetGUIColor();

				if(Target.Character.RebuildRequired(Target.Root)) {
					EditorGUILayout.HelpBox("Rebuild required because hierarchy was changed externally.", MessageType.Error);
					if(Utility.GUIButton("Build Hierarchy", Color.grey, Color.white)) {
						Target.Character.BuildHierarchy(Target.Root);
					}
				}

				if(Utility.GUIButton("Animation", UltiDraw.DarkGrey, UltiDraw.White)) {
					Target.Inspect = !Target.Inspect;
				}

				if(Target.Inspect) {
					using(new EditorGUILayout.VerticalScope ("Box")) {
						Target.ShowTrajectory = EditorGUILayout.Toggle("Show Trajectory", Target.ShowTrajectory);
						Target.ShowVelocities = EditorGUILayout.Toggle("Show Velocities", Target.ShowVelocities);
						Target.TargetBlending = EditorGUILayout.Slider("Target Blending", Target.TargetBlending, 0f, 1f);
						Target.StyleTransition = EditorGUILayout.Slider("Style Transition", Target.StyleTransition, 0f, 1f);
						Target.TrajectoryControl = EditorGUILayout.Toggle("Trajectory Control", Target.TrajectoryControl);
						Target.TrajectoryCorrection = EditorGUILayout.Slider("Trajectory Correction", Target.TrajectoryCorrection, 0f, 1f);

						EditorGUILayout.BeginHorizontal();
						if(Utility.GUIButton("Add IK Solver", UltiDraw.Brown, UltiDraw.White)) {
							Utility.Expand(ref Target.IKSolvers);
						}
						if(Utility.GUIButton("Remove IK Solver", UltiDraw.Brown, UltiDraw.White)) {
							Utility.Shrink(ref Target.IKSolvers);
						}
						EditorGUILayout.EndHorizontal();
						Target.SolveIK = EditorGUILayout.Toggle("Solve IK", Target.SolveIK);
						for(int i=0; i<Target.IKSolvers.Length; i++) {
							Target.IKSolvers[i] = (FootIK)EditorGUILayout.ObjectField(Target.IKSolvers[i], typeof(FootIK), true);
						}

						EditorGUI.BeginDisabledGroup(true);
						EditorGUILayout.ObjectField("Root", Target.Root, typeof(Transform), true);
						EditorGUI.EndDisabledGroup();
						Target.SetJointCount(EditorGUILayout.IntField("Joint Count", Target.Joints.Length));
						if(Utility.GUIButton("Auto Detect", UltiDraw.DarkGrey, UltiDraw.White)) {
							Target.AutoDetect();
						}
						for(int i=0; i<Target.Joints.Length; i++) {
							if(Target.Joints[i] != null) {
								Utility.SetGUIColor(UltiDraw.Green);
							} else {
								Utility.SetGUIColor(UltiDraw.Red);
							}
							EditorGUILayout.BeginHorizontal();
							EditorGUILayout.LabelField("Joint " + (i+1), GUILayout.Width(50f));
							Target.SetJoint(i, (Transform)EditorGUILayout.ObjectField(Target.Joints[i], typeof(Transform), true));
							EditorGUILayout.EndHorizontal();
							Utility.ResetGUIColor();
						}
					}
				}
			}
		}
	}
	#endif
}