// This is a sample main file for testing the wheel in a way it could be
// used in a game. It only works with the 9 first defines enabled
// (__WHEELS/_JOYSTICK to _AIRBORNE).

// The other defines show usages of the other methods of the Steering Wheel SDK

// Usage: the accelerator pedal is mapped to the fictitious speed of the
// car. If the pedal is not pressed at all, then the car is at a stop.
// In that case there is a damper that makes the wheel hard to turn, there
// is no spring effect. Surface effects are inexistent since at a stop you
//  wouldn't feel anything.
// As you start accelerating, the damper becomes loose and the spring
// force kicks in. If you start surface effects by hitting some of the
// buttons (see code below for button mapping), you will feel the surfaces
// in your wheel as long as you have some speed. You can also emulate side
// and front collisions by triggering certain buttons. Just like the
// surface effects, the collisions' magnitude is dependent on speed.

/*
The Logitech Steering Wheel SDK, including all accompanying documentation,
is protected by intellectual property laws. All rights
not expressly granted by Logitech are reserved.
*/

#include "stdafx.h"
#include "SteeringWheelSDKDemo.h"
#include "SteeringWheelSDKDemoDlg.h"

#ifdef _DEBUG
#define new DEBUG_NEW
#endif


#ifndef TRACE
#define TRACE LOGIWHEELTRACE
#endif

#define _DEMO // does things similar to how a game would feel based on pedal and button input

// CSteeringWheelSDKDemoDlg dialog

CSteeringWheelSDKDemoDlg::CSteeringWheelSDKDemoDlg(CWnd* pParent /*=NULL*/)
	: CDialog(CSteeringWheelSDKDemoDlg::IDD, pParent), m_isTimerActive(true), m_pWheelInputCalibrateDlg(NULL)
{
	m_hIcon = AfxGetApp()->LoadIcon(IDR_MAINFRAME);
}

void CSteeringWheelSDKDemoDlg::DoDataExchange(CDataExchange* pDX)
{
	CDialog::DoDataExchange(pDX);
}

BEGIN_MESSAGE_MAP(CSteeringWheelSDKDemoDlg, CDialog)
	ON_WM_PAINT()
	ON_WM_QUERYDRAGICON()
	ON_WM_TIMER()
	ON_WM_DESTROY()
	ON_MESSAGE(CALIBRATE_MSG, &CSteeringWheelSDKDemoDlg::OnCalibrateInput)
	ON_BN_CLICKED(IDC_BUTTON_GET_WHEEL_PROPERTIES0, &CSteeringWheelSDKDemoDlg::OnBnClickedButtonGetWheelProperties0)
	ON_BN_CLICKED(IDC_BUTTON_GET_WHEEL_PROPERTIES1, &CSteeringWheelSDKDemoDlg::OnBnClickedButtonGetWheelProperties1)
	ON_BN_CLICKED(IDC_BUTTON_DEFAULTS, &CSteeringWheelSDKDemoDlg::OnBnClickedButtonDefaults)
	ON_BN_CLICKED(IDC_BUTTON_SET_PREFERRED, &CSteeringWheelSDKDemoDlg::OnBnClickedButtonSetPreferred)
	ON_BN_KILLFOCUS(IDC_BUTTON_SET_PREFERRED, &CSteeringWheelSDKDemoDlg::OnBnKillfocusButtonSetPreferred)
	ON_BN_CLICKED(IDC_INIT, &CSteeringWheelSDKDemoDlg::OnBnClickedInit)
	ON_BN_CLICKED(IDC_CALIBRATE, &CSteeringWheelSDKDemoDlg::OnBnClickedCalibrate)
	ON_BN_CLICKED(IDC_CALIBRATE2, &CSteeringWheelSDKDemoDlg::OnBnClickedCalibrate2)
	ON_BN_CLICKED(IDC_SHUTDOWN, &CSteeringWheelSDKDemoDlg::OnBnClickedShutdown)
END_MESSAGE_MAP()

// CSteeringWheelSDKDemoDlg message handlers

BOOL CSteeringWheelSDKDemoDlg::OnInitDialog()
{
	CDialog::OnInitDialog();

	// Set the icon for this dialog.  The framework does this automatically
	//  when the application's main window is not a dialog
	SetIcon(m_hIcon, true);			// Set big icon
	SetIcon(m_hIcon, false);		// Set small icon

	SetTimer(1, 1000 / 30, NULL );

	return true;  // return true  unless you set the focus to a control
}

// If you add a minimize button to your dialog, you will need the code below
//  to draw the icon.  For MFC applications using the document/view model,
//  this is automatically done for you by the framework.
void CSteeringWheelSDKDemoDlg::OnPaint()
{
	if (IsIconic())
	{
		CPaintDC dc(this); // device context for painting

		SendMessage(WM_ICONERASEBKGND, reinterpret_cast<WPARAM>(dc.GetSafeHdc()), 0);

		// Center icon in client rectangle
		int cxIcon = GetSystemMetrics(SM_CXICON);
		int cyIcon = GetSystemMetrics(SM_CYICON);
		CRect rect;
		GetClientRect(&rect);
		int x = (rect.Width() - cxIcon + 1) / 2;
		int y = (rect.Height() - cyIcon + 1) / 2;

		// Draw the icon
		dc.DrawIcon(x, y, m_hIcon);
	}
	else
	{
		CDialog::OnPaint();
	}
}

// The system calls this function to obtain the cursor to display
// while the user drags the minimized window.
HCURSOR CSteeringWheelSDKDemoDlg::OnQueryDragIcon()
{
	return static_cast<HCURSOR>(m_hIcon);
}

void CSteeringWheelSDKDemoDlg::OnTimer(UINT_PTR nIDEvent)
{
	UNREFERENCED_PARAMETER(nIDEvent);

	int index_ = 0;
	float speedParam_[LOGI_MAX_CONTROLLERS] = {0.0f, 0.0f};
	float brakeParam_[LOGI_MAX_CONTROLLERS] = {0.0f, 0.0f};
	TCHAR sBuffer_[128];
	TCHAR* str_;
	TCHAR deviceConnected_[128][LOGI_MAX_CONTROLLERS];
	TCHAR dirtRoad_[128][LOGI_MAX_CONTROLLERS];
	TCHAR bumpyRoad_[128][LOGI_MAX_CONTROLLERS];
	TCHAR slipperyRoad_[128][LOGI_MAX_CONTROLLERS];
	TCHAR airborne_[128][LOGI_MAX_CONTROLLERS];

	int counter_ = 0;

	// Update the input device every timer message.

	//if the return value is false, means that the application has not been initialized yet and there is no hwnd available
	if(!LogiUpdate()) return;

	//if the timer is not active return immediately
	if(!m_isTimerActive) return;

	//// call this every frame in case a wheel gets plugged in.
	LogiGenerateNonLinearValues(0, -40);
	LogiGenerateNonLinearValues(1, 80);

	for (index_ = 0; index_ < LOGI_MAX_CONTROLLERS; index_++)
	{
		if (LogiIsConnected(index_))
		{
			// Find out if axes are separate or not. If combined, or
			// if we fail, use Y axis for gas and brake.
			wchar_t deviceName[128];
			LogiGetFriendlyProductName(0, deviceName, 128);
			wsprintf(deviceConnected_[index_], deviceName);

			m_DIJoyState2Device[index_] = LogiGetState(index_);
			
			if (LogiIsManufacturerConnected(index_, LOGI_MANUFACTURER_LOGITECH) && LogiIsDeviceConnected(index_, LOGI_DEVICE_TYPE_WHEEL))
			{
				LogiControllerPropertiesData propertiesData_;
				ZeroMemory(&propertiesData_, sizeof(propertiesData_));

				LogiGetCurrentControllerProperties(index_, propertiesData_);

				

				// calculate normalized speed parameter. In a real
				// game the parameter could go from 0 at a stop to 1
				// at a speed of about 50 to 80 miles/hour.
				if (propertiesData_.combinePedals)
				{
					speedParam_[index_] = max(((-(float)(GetControlValue(index_, ACCELERATOR))) / 32767), 0);
					brakeParam_[index_] = max((((float)(GetControlValue(index_, BRAKE))) / 32767), 0);
				}
				else
				{
					speedParam_[index_] = ((-(float)(GetControlValue(index_, ACCELERATOR))) / 65535) + float(0.5);
					brakeParam_[index_] = ((-(float)(GetControlValue(index_, BRAKE))) / 65535) + float(0.5);
				}
			}
			else if (LogiIsDeviceConnected(index_, LOGI_DEVICE_TYPE_WHEEL) && LogiIsManufacturerConnected(index_, LOGI_MANUFACTURER_MICROSOFT))
			{
				// Microsoft wheel
				wsprintf(deviceConnected_[index_], TEXT("Steering wheel, combined pedals"));
				speedParam_[index_] = max(((-(float)(GetControlValue(index_, ACCELERATOR))) / 32767), 0);
				brakeParam_[index_] = max((((float)(GetControlValue(index_, BRAKE))) / 32767), 0);
			}
			else if (LogiIsDeviceConnected(index_, LOGI_DEVICE_TYPE_WHEEL) && LogiIsManufacturerConnected(index_, LOGI_MANUFACTURER_OTHER) && m_DIJoyState2Device[index_] != NULL)
			{
				// Immersion wheel in combined mode
				if (m_DIJoyState2Device[index_]->lRz == 32767
					&& m_DIJoyState2Device[index_]->lY == 0
					&& m_DIJoyState2Device[index_]->rglSlider[0] != 0
					&& m_DIJoyState2Device[index_]->lZ== 0)
				{
					wsprintf(deviceConnected_[index_],	TEXT("Steering wheel, combined pedals"));
					speedParam_[index_] = max(((-(float)(m_DIJoyState2Device[index_]->rglSlider[0])) / 32767), 0);
					brakeParam_[index_] = max((((float)(m_DIJoyState2Device[index_]->rglSlider[0])) / 32767), 0);
				}

				// Immersion wheel in separate mode
				else if (m_DIJoyState2Device[index_]->lRz == 32767
					&& m_DIJoyState2Device[index_]->lY != 0
					&& m_DIJoyState2Device[index_]->rglSlider[0] != 0
					&& m_DIJoyState2Device[index_]->lZ== 0)
				{
					wsprintf(deviceConnected_[index_], TEXT("Steering wheel, separate pedals"));
					speedParam_[index_] = ((-(float)(m_DIJoyState2Device[index_]->lY)) / 65535) + float(0.5);
					// TODO: brake
				}
			}
			else if (LogiIsDeviceConnected(index_, LOGI_DEVICE_TYPE_JOYSTICK))
			{
				speedParam_[index_] = max(((-(float)(m_DIJoyState2Device[index_]->rglSlider[0])) / 32767), 0);
				brakeParam_[index_] = max((((float)(m_DIJoyState2Device[index_]->rglSlider[0])) / 32767), 0);
			}

			// Game pad
			else if (LogiIsDeviceConnected(index_, LOGI_DEVICE_TYPE_GAMEPAD))
			{
				speedParam_[index_] = max((((float)(m_DIJoyState2Device[index_]->lY)) / 32767), 0);
				brakeParam_[index_] = max(((-(float)(m_DIJoyState2Device[index_]->lY)) / 32767), 0);
			}
			else
			{
				wsprintf(deviceConnected_[index_], TEXT("Steering wheel"));
				speedParam_[index_] = 0;
				brakeParam_[index_] = 0;
			}

			LogiPlayLeds(index_, speedParam_[index_], 0.1f, 1.0f);

			// Play spring force
			LogiPlaySpringForce(index_,	0, int(70 * speedParam_[index_]), int(70 * speedParam_[index_]));

			// Play Damper Force
			LogiPlayDamperForce(index_, int(80 * (1 - speedParam_[index_])));

			// Play Collisions
			if (LogiButtonTriggered(index_, 0))
			{
				LogiPlaySideCollisionForce(index_, int(100 * speedParam_[index_]));
			}

			if (LogiButtonTriggered(index_, 1))
			{
				LogiPlaySideCollisionForce(index_, int(-100 * speedParam_[index_]));
			}

			if (LogiButtonTriggered(index_, 6))
			{
				LogiPlayFrontalCollisionForce(index_, int(100 * speedParam_[index_]));
			}

			// Play Bumpy road effect
			if (LogiIsPlaying(index_, LOGI_FORCE_BUMPY_ROAD))
			{
				LogiPlayBumpyRoadEffect(index_,	int(100 * speedParam_[index_]));
			}

			if (LogiButtonTriggered(index_, 3))
			{
				if (LogiIsPlaying(index_, LOGI_FORCE_BUMPY_ROAD))
				{
					LogiStopBumpyRoadEffect(index_);
				}
				else
				{
					LogiPlayBumpyRoadEffect(index_,	int(60 * speedParam_[index_]));
				}
			}

			// Play Dirt road effect
			if (LogiIsPlaying(index_, LOGI_FORCE_DIRT_ROAD))
			{
				LogiPlayDirtRoadEffect(index_, int(40 * speedParam_[index_]));
			}

			if (LogiButtonTriggered(index_, 2))
			{
				if (LogiIsPlaying(index_, LOGI_FORCE_DIRT_ROAD))
				{
					LogiStopDirtRoadEffect(index_);
				}
				else
				{
					LogiPlayDirtRoadEffect(index_, int(40 * speedParam_[index_]));
				}
			}

			// Play Slippery road effect
			if (LogiButtonTriggered(index_, 4))
			{
				if (LogiIsPlaying(index_, LOGI_FORCE_SLIPPERY_ROAD))
				{
					LogiStopSlipperyRoadEffect(index_);
				}
				else
				{
					LogiPlaySlipperyRoadEffect(index_, 70);
				}
			}

			// Play car in the air effect
			if (LogiButtonTriggered(index_, 5))
			{
				if (LogiIsPlaying(index_, LOGI_FORCE_CAR_AIRBORNE))
				{
					LogiStopCarAirborne(index_);
				}
				else
				{
					LogiPlayCarAirborne(index_);
				}
			}

			wsprintf(bumpyRoad_[index_], TEXT("off"));
			wsprintf(dirtRoad_[index_], TEXT("off"));
			wsprintf(slipperyRoad_[index_], TEXT("off"));
			wsprintf(airborne_[index_], TEXT("off"));

			if (LogiIsPlaying(index_, LOGI_FORCE_BUMPY_ROAD))
			{
				wsprintf(bumpyRoad_[index_], TEXT("on"));
			}
			if (LogiIsPlaying(index_, LOGI_FORCE_DIRT_ROAD))
			{
				wsprintf(dirtRoad_[index_], TEXT("on"));
			}
			if (LogiIsPlaying(index_, LOGI_FORCE_SLIPPERY_ROAD))
			{
				wsprintf(slipperyRoad_[index_], TEXT("on"));
			}
			if (LogiIsPlaying(index_, LOGI_FORCE_CAR_AIRBORNE))
			{
				wsprintf(airborne_[index_], TEXT("on"));
			}

			// Device 0
			if (index_ == 0)
			{
				// speed
				wsprintf(sBuffer_, TEXT("%ld"), int(1000 * speedParam_[index_]));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SPEED), sBuffer_);

				// brake
				wsprintf(sBuffer_, TEXT("%ld"), int(1000 * brakeParam_[index_]));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_BRAKE), sBuffer_);

				// Device connected
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_DEVICE), deviceConnected_[index_]);

				// Axes
				wsprintf(sBuffer_, TEXT("%ld"), m_DIJoyState2Device[index_]->lX);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_X_AXIS), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"),	m_DIJoyState2Device[index_]->lY);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Y_AXIS), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"),	m_DIJoyState2Device[index_]->lZ);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Z_AXIS), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"),	m_DIJoyState2Device[index_]->lRx);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_X_ROT), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"),	m_DIJoyState2Device[index_]->lRy);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Y_ROT), sBuffer_ );

				wsprintf(sBuffer_, TEXT("%ld"), m_DIJoyState2Device[index_]->lRz);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Z_ROT), sBuffer_);

				// Slider controls
				wsprintf(sBuffer_, TEXT("%ld"), m_DIJoyState2Device[index_]->rglSlider[0]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SLIDER0), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"),	m_DIJoyState2Device[index_]->rglSlider[1]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SLIDER1), sBuffer_);

				// Points of view
				wsprintf(sBuffer_, TEXT("%ld"), m_DIJoyState2Device[index_]->rgdwPOV[0]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_POV0), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"), m_DIJoyState2Device[index_]->rgdwPOV[1]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_POV1), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"),	m_DIJoyState2Device[index_]->rgdwPOV[2]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_POV2), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"),	m_DIJoyState2Device[index_]->rgdwPOV[3]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_POV3), sBuffer_);

				// Fill up text with which buttons are pressed
				str_ = sBuffer_;
				for(counter_ = 0; counter_ < 128; counter_++)
				{
					if (m_DIJoyState2Device[index_]->rgbButtons[counter_] & 0x80)
					{
						str_ += wsprintf(str_, TEXT("%02d "), counter_);
					}
				}
				*str_ = 0;   // Terminate the string

				::SetWindowText(::GetDlgItem(m_hWnd, IDC_BUTTONS), sBuffer_);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_DIRT_ROAD_PLAYING), dirtRoad_[0]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_BUMPY_ROAD_PLAYING), bumpyRoad_[0]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SLIPPERY_ROAD_PLAYING), slipperyRoad_[0]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_CAR_AIRBORNE_PLAYING), airborne_[0]);
			}

			// Device 1
			if (index_ == 1)
			{
				// speed
				wsprintf(sBuffer_, TEXT("%ld"), int(1000 * speedParam_[index_]));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SPEED2), sBuffer_);

				// brake
				wsprintf(sBuffer_, TEXT("%ld"), int(1000 * brakeParam_[index_]));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_BRAKE2), sBuffer_);

				// Device connected
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_DEVICE2), deviceConnected_[index_]);

				// Axes
				wsprintf(sBuffer_, TEXT("%ld"), m_DIJoyState2Device[index_]->lX);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_X_AXIS2), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"),	m_DIJoyState2Device[index_]->lY);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Y_AXIS2), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"),	m_DIJoyState2Device[index_]->lZ);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Z_AXIS2), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"), m_DIJoyState2Device[index_]->lRx);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_X_ROT2), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"), m_DIJoyState2Device[index_]->lRy);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Y_ROT2), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"),	m_DIJoyState2Device[index_]->lRz);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Z_ROT2), sBuffer_);

				// Slider controls
				wsprintf(sBuffer_, TEXT("%ld"), m_DIJoyState2Device[index_]->rglSlider[0]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SLIDER02), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"), m_DIJoyState2Device[index_]->rglSlider[1]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SLIDER12), sBuffer_);

				// Points of view
				wsprintf(sBuffer_, TEXT("%ld"), m_DIJoyState2Device[index_]->rgdwPOV[0]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_POV02), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"), m_DIJoyState2Device[index_]->rgdwPOV[1]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_POV12), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"),	m_DIJoyState2Device[index_]->rgdwPOV[2]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_POV22), sBuffer_);

				wsprintf(sBuffer_, TEXT("%ld"),	m_DIJoyState2Device[index_]->rgdwPOV[3]);
				::SetWindowText(::GetDlgItem( m_hWnd, IDC_POV32), sBuffer_);

				// Fill up text with which buttons are pressed
				str_ = sBuffer_;
				for(counter_ = 0; counter_ < 128; counter_++)
				{
					if (m_DIJoyState2Device[index_]->rgbButtons[counter_] & 0x80)
					{
						str_ += wsprintf(str_, TEXT("%02d "), counter_);
					}
				}
				*str_ = 0;   // Terminate the string

				::SetWindowText(::GetDlgItem(m_hWnd, IDC_BUTTONS2), sBuffer_);

				::SetWindowText(::GetDlgItem(m_hWnd, IDC_DIRT_ROAD_PLAYING2), dirtRoad_[index_]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_BUMPY_ROAD_PLAYING2), bumpyRoad_[index_]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SLIPPERY_ROAD_PLAYING2), slipperyRoad_[index_]);
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_CAR_AIRBORNE_PLAYING2), airborne_[index_]);
			}

		}
		else
		{
			if (index_ == 0)
			{
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_DEVICE ), _T("No device connected"));

				// speed
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SPEED), _T("0"));

				// brake
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_BRAKE), _T("0"));

				// Axes
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_X_AXIS ), _T("0"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Y_AXIS ), _T("0"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Z_AXIS ), _T("0"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_X_ROT), _T("0"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Y_ROT), _T("0"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Z_ROT), _T("0"));

				// Slider controls
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SLIDER0), _T("0"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SLIDER1), _T("0"));

				// Points of view
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_POV0), _T("-1"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_POV1), _T("-1"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_POV2), _T("-1"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_POV3), _T("-1"));

				::SetWindowText(::GetDlgItem(m_hWnd, IDC_BUTTONS), _T(""));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_DIRT_ROAD_PLAYING), _T("off"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_BUMPY_ROAD_PLAYING), _T("off"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SLIPPERY_ROAD_PLAYING), _T("off"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_CAR_AIRBORNE_PLAYING), _T("off"));
			}

			if (index_ == 1)
			{
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_DEVICE2), _T("No device connected"));

				// speed
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SPEED2), _T("0"));

				// brake
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_BRAKE2), _T("0"));

				// Axes
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_X_AXIS2), _T("0"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Y_AXIS2), _T("0"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Z_AXIS2), _T("0"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_X_ROT2), _T("0"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Y_ROT2), _T("0"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_Z_ROT2), _T("0"));

				// Slider controls
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SLIDER02), _T("0"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SLIDER12), _T("0"));

				// Points of view
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_POV02), _T("-1"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_POV12), _T("-1"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_POV22), _T("-1"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_POV32), _T("-1"));

				::SetWindowText(::GetDlgItem(m_hWnd, IDC_BUTTONS2 ), _T(""));

				::SetWindowText(::GetDlgItem(m_hWnd, IDC_DIRT_ROAD_PLAYING2), _T("off"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_BUMPY_ROAD_PLAYING2), _T("off"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_SLIPPERY_ROAD_PLAYING2), _T("off"));
				::SetWindowText(::GetDlgItem(m_hWnd, IDC_CAR_AIRBORNE_PLAYING2), _T("off"));
			}
		}
	}
}

void CSteeringWheelSDKDemoDlg::OnDestroy()
{
	KillTimer(1);
	LogiSteeringShutdown();
}

void CSteeringWheelSDKDemoDlg::OnBnClickedButtonGetWheelProperties0()
{
	LogiControllerPropertiesData propertiesData_;
	ZeroMemory(&propertiesData_, sizeof(propertiesData_));

	if (LogiIsConnected(0))
	{
		::SetWindowText(GetDlgItem(IDC_EDIT_WHEEL_PROPERTIES0)->m_hWnd, _T("SUCCEEDED"));

		LogiGetCurrentControllerProperties(0, propertiesData_);

		FillGetPropertiesFields(propertiesData_, LogiGetShifterMode(0));
	}
	else
	{
		::SetWindowText(GetDlgItem(IDC_EDIT_WHEEL_PROPERTIES0)->m_hWnd, _T("FAILED"));

		EmptyGetPropertiesFields();
	}

	::SetWindowText(GetDlgItem(IDC_EDIT_SET_PREFERRED)->m_hWnd, _T(""));
}

void CSteeringWheelSDKDemoDlg::OnBnClickedButtonGetWheelProperties1()
{
	LogiControllerPropertiesData propertiesData_;
	ZeroMemory(&propertiesData_, sizeof(propertiesData_));

	/*LogitechSteeringWheel::Wheel* wheel = LogitechSteeringWheel::Wheel::GetInstance();*/
	if (LogiIsConnected(1))
	{
		::SetWindowText(GetDlgItem(IDC_EDIT_WHEEL_PROPERTIES1)->m_hWnd, _T("SUCCEEDED"));

		LogiGetCurrentControllerProperties(1, propertiesData_);

		FillGetPropertiesFields(propertiesData_, LogiGetShifterMode(1));
	}
	else
	{
		::SetWindowText(GetDlgItem(IDC_EDIT_WHEEL_PROPERTIES1)->m_hWnd, _T("FAILED"));

		EmptyGetPropertiesFields();
	}

	::SetWindowText(GetDlgItem(IDC_EDIT_SET_PREFERRED)->m_hWnd, _T(""));
}

void CSteeringWheelSDKDemoDlg::FillGetPropertiesFields(CONST LogiControllerPropertiesData propertiesData, CONST int isGatedShifter)
{
	// Fill out all the fields
	TCHAR text_[MAX_PATH] = {'\0'};

	if (-1 == isGatedShifter)
	{
		::EnableWindow(GetDlgItem(IDC_STATIC_SHIFTER_GATED)->m_hWnd, false);
		((CButton*)GetDlgItem(IDC_CHECK_SHIFTER_GATED))->SetCheck(false);
	}
	else
	{
		::EnableWindow(GetDlgItem(IDC_STATIC_SHIFTER_GATED)->m_hWnd, true);
		((CButton*)GetDlgItem(IDC_CHECK_SHIFTER_GATED))->SetCheck(isGatedShifter);
	}

	((CButton*)GetDlgItem(IDC_CHECK_FORCE_ENABLED))->SetCheck(propertiesData.forceEnable);

	_itot_s(propertiesData.overallGain, text_, 10);
	::SetWindowText(GetDlgItem(IDC_EDIT_OVERALL_GAIN)->m_hWnd, text_);

	_itot_s(propertiesData.springGain, text_, 10);
	::SetWindowText(GetDlgItem(IDC_EDIT_SPRING_GAIN)->m_hWnd, text_);

	_itot_s(propertiesData.damperGain, text_, 10);
	::SetWindowText(GetDlgItem(IDC_EDIT_DAMPER_GAIN)->m_hWnd, text_);

	((CButton*)GetDlgItem(IDC_CHECKDEFAULT_SPRING_ENABLED))->SetCheck(propertiesData.defaultSpringEnabled);

	_itot_s(propertiesData.defaultSpringGain, text_, 10);
	::SetWindowText(GetDlgItem(IDC_EDIT_DEFAULT_SPRING_GAIN)->m_hWnd, text_);

	((CButton*)GetDlgItem(IDC_CHECK_PEDALS_COMBINED))->SetCheck(propertiesData.combinePedals);

	_itot_s(propertiesData.wheelRange, text_, 10);
	::SetWindowText(GetDlgItem(IDC_EDIT_DEGREES_TURN)->m_hWnd, text_);

	((CButton*)GetDlgItem(IDC_CHECK_USER_ALLOWS_SETTINGS))->SetCheck(propertiesData.allowGameSettings);
}

void CSteeringWheelSDKDemoDlg::EmptyGetPropertiesFields()
{
	// Empty Get fields
	TCHAR text_[MAX_PATH] = {'\0'};

	::EnableWindow(GetDlgItem(IDC_STATIC_SHIFTER_GATED)->m_hWnd, false);
	((CButton*)GetDlgItem(IDC_CHECK_SHIFTER_GATED))->SetCheck(false);
	((CButton*)GetDlgItem(IDC_CHECK_FORCE_ENABLED))->SetCheck(false);
	((CButton*)GetDlgItem(IDC_CHECKDEFAULT_SPRING_ENABLED))->SetCheck(false);
	((CButton*)GetDlgItem(IDC_CHECK_PEDALS_COMBINED))->SetCheck(false);
	((CButton*)GetDlgItem(IDC_CHECK_USER_ALLOWS_SETTINGS))->SetCheck(false);
	::SetWindowText(GetDlgItem(IDC_EDIT_OVERALL_GAIN)->m_hWnd, text_);
	::SetWindowText(GetDlgItem(IDC_EDIT_SPRING_GAIN)->m_hWnd, text_);
	::SetWindowText(GetDlgItem(IDC_EDIT_DAMPER_GAIN)->m_hWnd, text_);
	::SetWindowText(GetDlgItem(IDC_EDIT_DEFAULT_SPRING_GAIN)->m_hWnd, text_);
	::SetWindowText(GetDlgItem(IDC_EDIT_DEGREES_TURN)->m_hWnd, text_);

}

HRESULT CSteeringWheelSDKDemoDlg::RetrieveFieldsForSet(LogiControllerPropertiesData &propertiesData)
{
	TCHAR text_[MAX_PATH] = {'\0'};

	int enableCheck_ = ((CButton*)GetDlgItem(IDC_CHECK_FORCE_ENABLED))->GetCheck();

	::GetWindowText(GetDlgItem(IDC_EDIT_OVERALL_GAIN)->m_hWnd, text_, MAX_PATH);
	int overallGain_ = _wtoi(text_);
	if (overallGain_ < 0 || overallGain_ > 150 || 0 == _tcscmp(_T(""), text_))
	{
		::MessageBox(NULL, _T("Overall gain needs to be set between 0 and 150"), NULL, MB_OK);
		return E_FAIL;
	}

	::GetWindowText(GetDlgItem(IDC_EDIT_SPRING_GAIN)->m_hWnd, text_, MAX_PATH);
	int springGain_ = _wtoi(text_);
	if (springGain_ < 0 || springGain_ > 150 || 0 == _tcscmp(_T(""), text_))
	{
		::MessageBox(NULL, _T("Spring gain needs to be set between 0 and 150"), NULL, MB_OK);
		return E_FAIL;
	}

	::GetWindowText(GetDlgItem(IDC_EDIT_DAMPER_GAIN)->m_hWnd, text_, MAX_PATH);
	int damperGain_ = _wtoi(text_);
	if (damperGain_ < 0 || damperGain_ > 150 || 0 == _tcscmp(_T(""), text_))
	{
		::MessageBox(NULL, _T("Damper gain needs to be set between 0 and 150"), NULL, MB_OK);
		return E_FAIL;
	}

	int pedalsAreCombinedCheck_ = ((CButton*)GetDlgItem(IDC_CHECK_PEDALS_COMBINED))->GetCheck();

	::GetWindowText(GetDlgItem(IDC_EDIT_DEGREES_TURN)->m_hWnd, text_, MAX_PATH);
	int degreesOfTurn_ = _wtoi(text_);
	if (degreesOfTurn_ < 40 || degreesOfTurn_ > 900)
	{
		::MessageBox(NULL, _T("Degrees of turn needs to be set between 40 and 900"), NULL, MB_OK);
		return E_FAIL;
	}

	propertiesData.forceEnable = (enableCheck_ == (int)true);
	propertiesData.overallGain = overallGain_ ;
	propertiesData.springGain = springGain_ ;
	propertiesData.damperGain = damperGain_ ;
	propertiesData.combinePedals = (pedalsAreCombinedCheck_ == (int)true);
	propertiesData.wheelRange = degreesOfTurn_ ;

	return S_OK;
}

void CSteeringWheelSDKDemoDlg::OnBnClickedButtonDefaults()
{
	LogiControllerPropertiesData propertiesData_;
	ZeroMemory(&propertiesData_, sizeof(propertiesData_));

	propertiesData_.allowGameSettings = true;
	propertiesData_.combinePedals = false;
	propertiesData_.damperGain = 100;
	propertiesData_.defaultSpringEnabled = true;
	propertiesData_.defaultSpringGain = 100;
	propertiesData_.forceEnable = true;
	propertiesData_.gameSettingsEnabled = false;
	propertiesData_.overallGain = 100;
	propertiesData_.springGain = 100;
	propertiesData_.wheelRange = 200;

	FillGetPropertiesFields(propertiesData_, -1);

	::SetWindowText( GetDlgItem(IDC_EDIT_SET_PREFERRED)->m_hWnd, _T(""));
}

void CSteeringWheelSDKDemoDlg::OnBnClickedButtonSetPreferred()
{
	LogiControllerPropertiesData propertiesData_;
	ZeroMemory(&propertiesData_, sizeof(propertiesData_));

	if (SUCCEEDED(RetrieveFieldsForSet(propertiesData_)))
	{
		/*LogitechSteeringWheel::Wheel* wheel = LogitechSteeringWheel::Wheel::GetInstance();*/
		LogiSetPreferredControllerProperties(propertiesData_);

		::SetWindowText( GetDlgItem(IDC_EDIT_SET_PREFERRED)->m_hWnd, _T("DONE"));
	}
	else
	{
		::SetWindowText( GetDlgItem(IDC_EDIT_SET_PREFERRED)->m_hWnd, _T("FAILED"));
	}
}

void CSteeringWheelSDKDemoDlg::OnBnKillfocusButtonSetPreferred()
{
	::SetWindowText( GetDlgItem(IDC_EDIT_SET_PREFERRED)->m_hWnd, _T(""));
}


void CSteeringWheelSDKDemoDlg::OnBnClickedInit()
{
	LogiSteeringInitializeWithWindow(true, m_hWnd);
}


void CSteeringWheelSDKDemoDlg::OnBnClickedCalibrate()
{
	ShowInputCalibrate(0);
}


void CSteeringWheelSDKDemoDlg::OnBnClickedCalibrate2()
{
	ShowInputCalibrate(1);
}

//Creates a calibration dialog that will be responsible for capturing input configuration and returning it to this dialog.
void CSteeringWheelSDKDemoDlg::ShowInputCalibrate(int device)
{
	m_isTimerActive = false;
	m_calibrateDevice = device;
	LogiSteeringShutdown();

	m_pWheelInputCalibrateDlg = new CSteeringWheelInputCalibrateDlg(device, m_hWnd, FromHandle(m_hWnd));
	if (m_pWheelInputCalibrateDlg != NULL)
	{
		if (!m_pWheelInputCalibrateDlg->Create(IDD_INPUTCALIBRATE_DIALOG, this))
		{
			AfxMessageBox(_T("Error creating Dialog"));
		}

		m_pWheelInputCalibrateDlg->ShowWindow(SW_SHOW);
		EnableWindow(false);
	}
}

//Receive messages from the calibration dialog and store them in a temporary control map until the changes are accepted.
LRESULT CSteeringWheelSDKDemoDlg::OnCalibrateInput(WPARAM wParam, LPARAM lParam)
{
	if (wParam == SAVE || wParam == CANCEL)
	{
		EnableWindow(true);
		LogiSteeringInitializeWithWindow(true, m_hWnd);
		m_isTimerActive = true;
	}
	if (wParam == SAVE)
	{
		m_controlMap[m_calibrateDevice] = m_tempMap[m_calibrateDevice];
		m_tempMap[m_calibrateDevice].clear();
	}
	if (wParam == CANCEL)
	{
		m_tempMap[m_calibrateDevice].clear();
	}
	if (wParam != SAVE && wParam != CANCEL)
	{
		m_tempMap[m_calibrateDevice][wParam] = lParam;
	}
	return 0;
}

void CSteeringWheelSDKDemoDlg::OnBnClickedShutdown()
{
	LogiSteeringShutdown();
}

//Get the current access value for the device at the mapped control.
//Returns a default value if no control is mapped.
long CSteeringWheelSDKDemoDlg::GetControlValue(int device, int control)
{
	if (control == WHEEL)
	{
		switch (m_controlMap[device][control])
		{
		case X_AXIS:
			return m_DIJoyState2Device[device]->lX;
		case Y_AXIS:
			return m_DIJoyState2Device[device]->lY;
		case Z_AXIS:
			return m_DIJoyState2Device[device]->lZ;
		case SLIDER_0:
			return m_DIJoyState2Device[device]->rglSlider[0];
		case SLIDER_1:
			return m_DIJoyState2Device[device]->rglSlider[1];
		case X_ROT:
			return m_DIJoyState2Device[device]->lRx;
		case Y_ROT:
			return m_DIJoyState2Device[device]->lRy;
		case Z_ROT:
			return m_DIJoyState2Device[device]->lRz;
		default:
			return m_DIJoyState2Device[device]->lX;
		}
	}
	if (control == ACCELERATOR)
	{
		switch (m_controlMap[device][control])
		{
		case X_AXIS:
			return m_DIJoyState2Device[device]->lX;
		case Y_AXIS:
			return m_DIJoyState2Device[device]->lY;
		case Z_AXIS:
			return m_DIJoyState2Device[device]->lZ;
		case SLIDER_0:
			return m_DIJoyState2Device[device]->rglSlider[0];
		case SLIDER_1:
			return m_DIJoyState2Device[device]->rglSlider[1];
		case X_ROT:
			return m_DIJoyState2Device[device]->lRx;
		case Y_ROT:
			return m_DIJoyState2Device[device]->lRy;
		case Z_ROT:
			return m_DIJoyState2Device[device]->lRz;
		default:
			return m_DIJoyState2Device[device]->lY;
		}
	}
	if (control == BRAKE)
	{
		switch (m_controlMap[device][control])
		{
		case X_AXIS:
			return m_DIJoyState2Device[device]->lX;
		case Y_AXIS:
			return m_DIJoyState2Device[device]->lY;
		case Z_AXIS:
			return m_DIJoyState2Device[device]->lZ;
		case SLIDER_0:
			return m_DIJoyState2Device[device]->rglSlider[0];
		case SLIDER_1:
			return m_DIJoyState2Device[device]->rglSlider[1];
		case X_ROT:
			return m_DIJoyState2Device[device]->lRx;
		case Y_ROT:
			return m_DIJoyState2Device[device]->lRy;
		case Z_ROT:
			return m_DIJoyState2Device[device]->lRz;
		default:
			return m_DIJoyState2Device[device]->lRz;
		}
	}
	if (control == CLUTCH)
	{
		switch (m_controlMap[device][control])
		{
		case X_AXIS:
			return m_DIJoyState2Device[device]->lX;
		case Y_AXIS:
			return m_DIJoyState2Device[device]->lY;
		case Z_AXIS:
			return m_DIJoyState2Device[device]->lZ;
		case SLIDER_0:
			return m_DIJoyState2Device[device]->rglSlider[0];
		case SLIDER_1:
			return m_DIJoyState2Device[device]->rglSlider[1];
		case X_ROT:
			return m_DIJoyState2Device[device]->lRx;
		case Y_ROT:
			return m_DIJoyState2Device[device]->lRy;
		case Z_ROT:
			return m_DIJoyState2Device[device]->lRz;
		default:
			return m_DIJoyState2Device[device]->rglSlider[0];
		}
	}

	return 0;
}