using UnityEngine;

public class SeatOffset : StateMachineBehaviour
{
    public Vector3 localPosition;     // 이 클립에 맞는 위치
    public Vector3 localEulerAngles;  // 필요하면 미세 회전

    public override void OnStateEnter(Animator animator, AnimatorStateInfo info, int layer)
    {
        animator.transform.localPosition = localPosition;
        animator.transform.localEulerAngles = localEulerAngles;
    }
}