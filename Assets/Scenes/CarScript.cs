using UnityEngine;

public class CarMover : MonoBehaviour
{
    public float radius = 2f;   // Radius of the circle
    public float duration = 60f; // Duration for a full rotation (in seconds)

    private float speed; // Speed of rotation
    private float startTime;

    void Start()
    {
        // Calculate the speed based on the duration for one full circle
        speed = 2 * Mathf.PI / duration; // Full rotation in one minute
        startTime = Time.time; // Record start time
    }

    void Update()
    {
        float timeElapsed = Time.time - startTime;
        float angle = timeElapsed * speed; // Calculate the current angle

        // Use Sin and Cos to calculate circular movement along the X and Z axes
        float x = Mathf.Cos(angle) * radius;
        float z = Mathf.Sin(angle) * radius;

        // Apply the position to the car object
        transform.position = new Vector3(x, transform.position.y, z);
    }
}