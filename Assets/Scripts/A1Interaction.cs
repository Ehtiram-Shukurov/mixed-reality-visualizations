using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using Oculus.Interaction.Input;

public class A1Interaction : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    [SerializeField]
    private Hand leftHand; // TO DO: assign OVRLeftHandDataSource to this in the inspector
    [SerializeField]
    private Hand rightHand; // TO DO: assign OVRRightHandDataSource to this in the inspector

    [SerializeField]
    private TextMeshPro LeftHandPosText; // TO DO: assign a TextMeshPro object in the inspector

    [SerializeField]
    private TextMeshPro RightHandPosText; // TO DO: assign a TextMeshPro object in the inspector

    [SerializeField]
    private TextMeshPro StateText; // TO DO: assign a TextMeshPro object in the inspector

    private Pose leftHandPose;
    private Pose rightHandPose;
    private HandJointId handJointId = HandJointId.HandIndex3; // Note: you can change this to any bone you want, such as HandThumbTip, HandMiddleTip, etc.
    private bool isThumbsUp = false;


    void Update()
    {
        leftHand.GetJointPose(handJointId, out leftHandPose);
        // 1. display the position and rotation of the left hand joint in the LeftHandPosText
        LeftHandPosText.text =$"Left {handJointId}\n" +$"Pos: {leftHandPose.position}\n" +$"Rot: {leftHandPose.rotation.eulerAngles}";

        rightHand.GetJointPose(handJointId, out rightHandPose);
        // 2. display the position and rotation of the right hand joint in the RightHandPosText
        RightHandPosText.text = $"Right {handJointId}\n" + $"Pos: {rightHandPose.position}\n" + $"Rot: {rightHandPose.rotation.eulerAngles}";

        // 3. if two hand poses are close to each other, display a message in the StateText
        float dist = Vector3.Distance(leftHandPose.position, rightHandPose.position);

        if (!isThumbsUp)
        {
            if (dist < 0.05f)
                StateText.text = $"Hands are close ({dist:F3} m)";
            else
                StateText.text = $"Hands distance: {dist:F3} m";
        }

    }

    public void ThumbsUp()
    {
        // 4. when the user gives a thumbs up gesture, display a message in the StateText
        isThumbsUp = true;
        StateText.text = "Thumbs Up!";
    }

    public void NoThumbsUp()
    {
        // 5. when the user gives a no thumbs up gesture, display a message in the StateText
        isThumbsUp = false;
        StateText.text = "No Thumbs Up!";
    }
}
