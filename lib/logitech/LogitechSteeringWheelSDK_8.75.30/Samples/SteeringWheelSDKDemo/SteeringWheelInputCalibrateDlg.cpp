// DialogInputCalibrate.cpp : implementation file
//

#include "stdafx.h"
#include "SteeringWheelSDKDemo.h"
#include "SteeringWheelInputCalibrateDlg.h"
#include "afxdialogex.h"

IMPLEMENT_DYNAMIC(CSteeringWheelInputCalibrateDlg, CDialogEx)

CSteeringWheelInputCalibrateDlg::CSteeringWheelInputCalibrateDlg(int deviceIndex, HWND hParent, CWnd* pParent /*=NULL*/)
: CDialogEx(CSteeringWheelInputCalibrateDlg::IDD, pParent), deviceIndex(deviceIndex), hParent(hParent), m_calibrationAction(0), m_controlCurrent(SKIP)
{
	m_controlSteps[WHEEL]		= _T("Wheel");
	m_controlSteps[ACCELERATOR] = _T("Accelerator");
	m_controlSteps[BRAKE]		= _T("Brake");
	m_controlSteps[CLUTCH]		= _T("Clutch");

	m_controlMessages[WHEEL]		= _T("Action: Turn the wheel left or right.");
	m_controlMessages[ACCELERATOR]	= _T("Action: Tap the accelerator.");
	m_controlMessages[BRAKE]		= _T("Action: Tap the brake.");
	m_controlMessages[CLUTCH]		= _T("Action: Tap the clutch.");

	m_controlNames[SKIP]		= _T("None");
	m_controlNames[X_AXIS]		= _T("X Axis");
	m_controlNames[Y_AXIS]		= _T("Y Axis");
	m_controlNames[Z_AXIS]		= _T("Z Axis");
	m_controlNames[SLIDER_0]	= _T("Slider 0");
	m_controlNames[SLIDER_1]	= _T("Slider 1");
	m_controlNames[X_ROT]		= _T("X Rot");
	m_controlNames[Y_ROT]		= _T("Y Rot");
	m_controlNames[Z_ROT]		= _T("Z Rot");

	m_controlStatus[SKIP]		= _T("New control: None");
	m_controlStatus[X_AXIS]		= _T("New control: X Axis");
	m_controlStatus[Y_AXIS]		= _T("New control: Y Axis");
	m_controlStatus[Z_AXIS]		= _T("New control: Z Axis");
	m_controlStatus[SLIDER_0]	= _T("New control: Slider 0");
	m_controlStatus[SLIDER_1]	= _T("New control: Slider 1");
	m_controlStatus[X_ROT]		= _T("New control: X Rot");
	m_controlStatus[Y_ROT]		= _T("New control: Y Rot");
	m_controlStatus[Z_ROT]		= _T("New control: Z Rot");
}

CSteeringWheelInputCalibrateDlg::~CSteeringWheelInputCalibrateDlg()
{
}

void CSteeringWheelInputCalibrateDlg::DoDataExchange(CDataExchange* pDX)
{
	CDialogEx::DoDataExchange(pDX);
}

BEGIN_MESSAGE_MAP(CSteeringWheelInputCalibrateDlg, CDialogEx)
	ON_WM_TIMER()
	ON_BN_CLICKED(IDC_CANCEL, &CSteeringWheelInputCalibrateDlg::OnBnClickedCancel)
	ON_BN_CLICKED(IDC_SKIP, &CSteeringWheelInputCalibrateDlg::OnBnClickedSkip)
	ON_BN_CLICKED(IDC_ACCEPT, &CSteeringWheelInputCalibrateDlg::OnBnClickedAccept)
	ON_BN_CLICKED(IDC_NEXT, &CSteeringWheelInputCalibrateDlg::OnBnClickedNext)
END_MESSAGE_MAP()

// CSteeringWheelInputCalibrateDlg message handlers

BOOL CSteeringWheelInputCalibrateDlg::OnInitDialog()
{
	CDialog::OnInitDialog();

	LogiSteeringInitializeWithWindow(true, m_hWnd);

	DIJOYSTATE2 *DIJoyState2_;
	m_lastDIJoyState2 = NULL;

	if (LogiIsConnected(deviceIndex))
	{
		DIJoyState2_ = LogiGetState(deviceIndex);
		m_lastDIJoyState2 = new DIJOYSTATE2(*DIJoyState2_);
	}

	UpdateText();
	SetTimer(2, 1000 / 30, NULL);

	return TRUE;  // return TRUE  unless you set the focus to a control
}

void CSteeringWheelInputCalibrateDlg::OnTimer(UINT_PTR nIDEvent)
{
	UNREFERENCED_PARAMETER(nIDEvent);

	DIJOYSTATE2 *DIJoyState2_;

	if (!LogiUpdate()) return;

	if (LogiIsConnected(deviceIndex))
	{
		DIJoyState2_ = LogiGetState(deviceIndex);

		// If there is a change in axis values and at least a second has passed since the axis was last registered.
		if (DIJoyState2_->lX != m_lastDIJoyState2->lX)
		{
			m_controlCurrent = X_AXIS;
		}
		if (DIJoyState2_->lY != m_lastDIJoyState2->lY)
		{
			m_controlCurrent = Y_AXIS;
		}
		if (DIJoyState2_->lZ != m_lastDIJoyState2->lZ)
		{
			m_controlCurrent = Z_AXIS;
		}
		if (DIJoyState2_->rglSlider[0] != m_lastDIJoyState2->rglSlider[0])
		{
			m_controlCurrent = SLIDER_0;
		}
		if (DIJoyState2_->rglSlider[1] != m_lastDIJoyState2->rglSlider[1])
		{
			m_controlCurrent = SLIDER_1;
		}
		if (DIJoyState2_->lRx != m_lastDIJoyState2->lRx)
		{
			m_controlCurrent = X_ROT;
		}
		if (DIJoyState2_->lRy != m_lastDIJoyState2->lRy)
		{
			m_controlCurrent = Y_ROT;
		}
		if (DIJoyState2_->lRz != m_lastDIJoyState2->lRz)
		{
			m_controlCurrent = Z_ROT;
		}

		delete m_lastDIJoyState2;
		m_lastDIJoyState2 = new DIJOYSTATE2(*DIJoyState2_);

		if (m_calibrationAction <= 3)
		{
			UpdateText();
		}
	}
}

void CSteeringWheelInputCalibrateDlg::OnBnClickedSkip()
{
	m_controlChanges[m_calibrationAction] = m_controlNames[SKIP];
	m_controlCurrent = SKIP;
	m_calibrationAction++;

	// We are done collecting input
	if (m_calibrationAction == 4)
	{
		ShowAccept();
	}
}

void CSteeringWheelInputCalibrateDlg::OnBnClickedNext()
{
	m_controlChanges[m_calibrationAction] = m_controlNames[m_controlCurrent];
	::PostMessage(hParent, CALIBRATE_MSG, (WPARAM)m_calibrationAction, (LPARAM)m_controlCurrent);
	m_controlCurrent = SKIP;
	m_calibrationAction++;

	// We are done collecting input
	if (m_calibrationAction == 4)
	{
		ShowAccept();
	}
}

void CSteeringWheelInputCalibrateDlg::OnBnClickedAccept()
{
	::PostMessage(hParent, CALIBRATE_MSG, (WPARAM)SAVE, (LPARAM)NULL);
	ExitWindow();
}

void CSteeringWheelInputCalibrateDlg::OnBnClickedCancel()
{
	::PostMessage(hParent, CALIBRATE_MSG, (WPARAM)CANCEL, (LPARAM)NULL);
	ExitWindow();
}

void CSteeringWheelInputCalibrateDlg::UpdateText()
{
	::SetWindowText(::GetDlgItem(m_hWnd, IDC_NEWCONTROL), m_controlStatus[m_controlCurrent]);
	::SetWindowText(::GetDlgItem(m_hWnd, IDC_ACTION), m_controlMessages[m_calibrationAction]);
}

void CSteeringWheelInputCalibrateDlg::ShowAccept()
{
	TCHAR sBuffer_[512] = {'\0'};

	GetDlgItem(IDC_SKIP)->ShowWindow(SW_HIDE);
	GetDlgItem(IDC_NEXT)->ShowWindow(SW_HIDE);
	GetDlgItem(IDC_NEWCONTROL)->ShowWindow(SW_HIDE);
	GetDlgItem(IDC_ACTION)->ShowWindow(SW_HIDE);
	GetDlgItem(IDC_ACCEPT)->ShowWindow(SW_SHOW);
	GetDlgItem(IDC_CHANGES)->ShowWindow(SW_SHOW);

	for (int i = 0; i < 4; i++)
	{
		wsprintf(sBuffer_, TEXT("%s%s%s%s%s"), sBuffer_, m_controlSteps[i], _T(": "), m_controlChanges[i], _T("\n"));
	}
	::SetWindowText(::GetDlgItem(m_hWnd, IDC_CHANGES), sBuffer_);
}

void CSteeringWheelInputCalibrateDlg::ExitWindow()
{
	KillTimer(2);
	LogiSteeringShutdown();
	CDialogEx::OnOK();
}