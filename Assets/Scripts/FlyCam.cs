using System;
using UnityEngine;

public class FlyCam : MonoBehaviour
{
    public float hSensitivity;
    public float vSensitivity;
    public float speed;
    private Transform tForm;
    private float rotationX, rotationY;

    private void Awake()
    {
        tForm = GetComponent<Transform>();
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
            Cursor.lockState = (Cursor.lockState == CursorLockMode.None)
                ? CursorLockMode.Locked
                : CursorLockMode.None;

        if (!Input.GetMouseButton(1)) return;
        rotationX += Input.GetAxis("Mouse X") * hSensitivity * Time.deltaTime;
        rotationY += Input.GetAxis("Mouse Y") * vSensitivity * Time.deltaTime;
        rotationY = Mathf.Clamp(rotationY, -90, 90);

        tForm.localRotation = Quaternion.AngleAxis(rotationX, Vector3.up);
        tForm.localRotation *= Quaternion.AngleAxis(rotationY, Vector3.left);

        tForm.position += tForm.forward * speed * Input.GetAxis("Vertical") * Time.deltaTime;
        tForm.position += tForm.right * speed * Input.GetAxis("Horizontal") * Time.deltaTime;

        if (Input.GetKey(KeyCode.Q))
        {
            tForm.position += tForm.up * speed * Time.deltaTime;
        }

        if (Input.GetKey(KeyCode.E))
        {
            tForm.position -= tForm.up * speed * Time.deltaTime;
        }
    }
}