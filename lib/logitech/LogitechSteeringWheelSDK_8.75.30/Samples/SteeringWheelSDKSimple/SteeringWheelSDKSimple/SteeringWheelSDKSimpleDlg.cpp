
// DemoWheelSimpleDlg.cpp : implementation file
//

#include "stdafx.h"
#include "SteeringWheelSDKSimple.h"
#include "SteeringWheelSDKSimpleDlg.h"
#include "afxdialogex.h"

#ifdef _DEBUG
#define new DEBUG_NEW
#endif

CSteeringWheelSimpleDlg::CSteeringWheelSimpleDlg(CWnd* pParent /*=NULL*/)
	: CDialogEx(CSteeringWheelSimpleDlg::IDD, pParent)
{
	m_hIcon = AfxGetApp()->LoadIcon(IDR_MAINFRAME);
}

void CSteeringWheelSimpleDlg::DoDataExchange(CDataExchange* pDX)
{
	CDialogEx::DoDataExchange(pDX);
	DDX_Control(pDX, IDC_SCROLLOFFSET, m_scrollBarOffset);
	DDX_Control(pDX, IDC_SCROLLSATURATION, m_scrollBarSaturation);
	DDX_Control(pDX, IDC_SCROLLCOEFFICIENT, m_scrollBarCoefficient);
	DDX_Control(pDX, IDC_SCROLLMAGNITUDE, m_scrollBarMagnitude);
	DDX_Control(pDX, IDC_SCROLLUSABLERANGE, m_scrollBarUsableRange);
	DDX_Control(pDX, IDC_CHECKALLDEVICES, m_checkAllDevices);
	DDX_Control(pDX, IDC_CHECKFULLRANGE, m_checkFullRangePercentages);
	DDX_Control(pDX, IDC_CHECKSPRING, m_checkSpring);
	DDX_Control(pDX, IDC_CHECKCONSTANT, m_checkConstant);
	DDX_Control(pDX, IDC_CHECKDAMPER, m_checkDamper);
	DDX_Control(pDX, IDC_CHECKDIRTROAD, m_checkDirtRoad);
	DDX_Control(pDX, IDC_CHECKBUMPYROAD, m_checkBumpyRoad);
	DDX_Control(pDX, IDC_CHECKSLIPPERYROAD, m_checkSlipperyRoad);
	DDX_Control(pDX, IDC_CHECKCARAIRBORNE, m_checkCarAirborne);
	DDX_Control(pDX, IDC_CHECKSOFTSTOP, m_checkSoftstop);
}

BEGIN_MESSAGE_MAP(CSteeringWheelSimpleDlg, CDialogEx)
	ON_WM_SYSCOMMAND()
	ON_WM_PAINT()
	ON_WM_QUERYDRAGICON()
	ON_WM_HSCROLL()
	ON_WM_TIMER()
	ON_BN_CLICKED(IDC_EXIT, &CSteeringWheelSimpleDlg::OnBnClickedExit)
	ON_BN_CLICKED(IDC_LogiSteeringInitialize, &CSteeringWheelSimpleDlg::OnBnClickedLogiSteeringInitialize)
	ON_BN_CLICKED(IDC_LogiSteeringInitializeWithWindow, &CSteeringWheelSimpleDlg::OnBnClickedLogiSteeringInitializeWithWindow)
	ON_BN_CLICKED(IDC_LogiSteeringGetSdkVersion, &CSteeringWheelSimpleDlg::OnBnClickedLogiSteeringGetSdkVersion)
	ON_BN_CLICKED(IDC_LogiUpdate, &CSteeringWheelSimpleDlg::OnBnClickedLogiUpdate)
	ON_BN_CLICKED(IDC_LogiGetStateENGINES, &CSteeringWheelSimpleDlg::OnBnClickedLogiGetStateENGINES)
	ON_BN_CLICKED(IDC_LogiGetState, &CSteeringWheelSimpleDlg::OnBnClickedLogiGetState)
	ON_BN_CLICKED(IDC_LogiGetFriendlyProductName, &CSteeringWheelSimpleDlg::OnBnClickedLogiGetFriendlyProductName)
	ON_BN_CLICKED(IDC_LogiIsConnected, &CSteeringWheelSimpleDlg::OnBnClickedLogiIsConnected)
	ON_BN_CLICKED(IDC_LogiIsDeviceConnected, &CSteeringWheelSimpleDlg::OnBnClickedLogiIsDeviceConnected)
	ON_BN_CLICKED(IDC_LogiIsManufacturerConnected, &CSteeringWheelSimpleDlg::OnBnClickedLogiIsManufacturerConnected)
	ON_BN_CLICKED(IDC_LogiIsModelConnected, &CSteeringWheelSimpleDlg::OnBnClickedLogiIsModelConnected)
	ON_BN_CLICKED(IDC_LogiButtonTriggered, &CSteeringWheelSimpleDlg::OnBnClickedLogiButtonTriggered)
	ON_BN_CLICKED(IDC_LogiButtonReleased, &CSteeringWheelSimpleDlg::OnBnClickedLogiButtonReleased)
	ON_BN_CLICKED(IDC_LogiButtonIsPressed, &CSteeringWheelSimpleDlg::OnBnClickedLogiButtonIsPressed)
	ON_BN_CLICKED(IDC_LogiGenerateNonLinearValues, &CSteeringWheelSimpleDlg::OnBnClickedLogiGenerateNonLinearValues)
	ON_BN_CLICKED(IDC_LogiGetNonLinearValue, &CSteeringWheelSimpleDlg::OnBnClickedLogiGetNonLinearValue)
	ON_BN_CLICKED(IDC_LogiHasForceFeedback, &CSteeringWheelSimpleDlg::OnBnClickedLogiHasForceFeedback)
	ON_BN_CLICKED(IDC_LogiIsPlaying, &CSteeringWheelSimpleDlg::OnBnClickedLogiIsPlaying)
	ON_BN_CLICKED(IDC_LogiPlaySpringForce, &CSteeringWheelSimpleDlg::OnBnClickedLogiPlaySpringForce)
	ON_BN_CLICKED(IDC_LogiStopSpringForce, &CSteeringWheelSimpleDlg::OnBnClickedLogiStopSpringForce)
	ON_BN_CLICKED(IDC_LogiPlayConstantForce, &CSteeringWheelSimpleDlg::OnBnClickedLogiPlayConstantForce)
	ON_BN_CLICKED(IDC_LogiStopConstantForce, &CSteeringWheelSimpleDlg::OnBnClickedLogiStopConstantForce)
	ON_BN_CLICKED(IDC_LogiPlayDamperForce, &CSteeringWheelSimpleDlg::OnBnClickedLogiPlayDamperForce)
	ON_BN_CLICKED(IDC_LogiStopDamperForce, &CSteeringWheelSimpleDlg::OnBnClickedLogiStopDamperForce)
	ON_BN_CLICKED(IDC_LogiPlaySideCollisionForce, &CSteeringWheelSimpleDlg::OnBnClickedLogiPlaySideCollisionForce)
	ON_BN_CLICKED(IDC_LogiPlayFrontalCollisionForce, &CSteeringWheelSimpleDlg::OnBnClickedLogiPlayFrontalCollisionForce)
	ON_BN_CLICKED(IDC_LogiPlayDirtRoadEffect, &CSteeringWheelSimpleDlg::OnBnClickedLogiPlayDirtRoadEffect)
	ON_BN_CLICKED(IDC_LogiStopDirtRoadEffect, &CSteeringWheelSimpleDlg::OnBnClickedLogiStopDirtRoadEffect)
	ON_BN_CLICKED(IDC_LogiPlayBumpyRoadEffect, &CSteeringWheelSimpleDlg::OnBnClickedLogiPlayBumpyRoadEffect)
	ON_BN_CLICKED(IDC_LogiStopBumpyRoadEffect, &CSteeringWheelSimpleDlg::OnBnClickedLogiStopBumpyRoadEffect)
	ON_BN_CLICKED(IDC_LogiPlaySlipperyRoadEffect, &CSteeringWheelSimpleDlg::OnBnClickedLogiPlaySlipperyRoadEffect)
	ON_BN_CLICKED(IDC_LogiStopSlipperyRoadEffect, &CSteeringWheelSimpleDlg::OnBnClickedLogiStopSlipperyRoadEffect)
	ON_BN_CLICKED(IDC_LogiPlaySurfaceEffect, &CSteeringWheelSimpleDlg::OnBnClickedLogiPlaySurfaceEffect)
	ON_BN_CLICKED(IDC_LogiStopSurfaceEffect, &CSteeringWheelSimpleDlg::OnBnClickedLogiStopSurfaceEffect)
	ON_BN_CLICKED(IDC_LogiPlayCarAirborne, &CSteeringWheelSimpleDlg::OnBnClickedLogiPlayCarAirborne)
	ON_BN_CLICKED(IDC_LogiStopCarAirborne, &CSteeringWheelSimpleDlg::OnBnClickedLogiStopCarAirborne)
	ON_BN_CLICKED(IDC_LogiPlaySoftstopForce, &CSteeringWheelSimpleDlg::OnBnClickedLogiPlaySoftstopForce)
	ON_BN_CLICKED(IDC_LogiStopSoftstopForce, &CSteeringWheelSimpleDlg::OnBnClickedLogiStopSoftstopForce)
	ON_BN_CLICKED(IDC_LogiSetPreferredControllerProperties, &CSteeringWheelSimpleDlg::OnBnClickedLogiSetPreferredControllerProperties)
	ON_BN_CLICKED(IDC_LogiGetCurrentControllerProperties, &CSteeringWheelSimpleDlg::OnBnClickedLogiGetCurrentControllerProperties)
	ON_BN_CLICKED(IDC_LogiGetShifterMode, &CSteeringWheelSimpleDlg::OnBnClickedLogiGetShifterMode)
	ON_BN_CLICKED(IDC_LogiPlayLeds, &CSteeringWheelSimpleDlg::OnBnClickedLogiPlayLeds)
	ON_BN_CLICKED(IDC_LogiSteeringShutdown, &CSteeringWheelSimpleDlg::OnBnClickedLogiSteeringShutdown)
	ON_EN_CHANGE(IDC_DEVICE, &CSteeringWheelSimpleDlg::OnEnChangeDevice)
	ON_BN_CLICKED(IDC_CHECKALLDEVICES, &CSteeringWheelSimpleDlg::OnBnClickedCheckAllDevices)
	ON_BN_CLICKED(IDC_CHECKSPRING, &CSteeringWheelSimpleDlg::OnBnClickedCheckSpring)
	ON_BN_CLICKED(IDC_CHECKCONSTANT, &CSteeringWheelSimpleDlg::OnBnClickedCheckConstant)
	ON_BN_CLICKED(IDC_CHECKDAMPER, &CSteeringWheelSimpleDlg::OnBnClickedCheckDamper)
	ON_BN_CLICKED(IDC_CHECKDIRTROAD, &CSteeringWheelSimpleDlg::OnBnClickedCheckDirtRoad)
	ON_BN_CLICKED(IDC_CHECKBUMPYROAD, &CSteeringWheelSimpleDlg::OnBnClickedCheckBumpyRoad)
	ON_BN_CLICKED(IDC_CHECKSLIPPERYROAD, &CSteeringWheelSimpleDlg::OnBnClickedCheckSlipperyRoad)
	ON_BN_CLICKED(IDC_CHECKCARAIRBORNE, &CSteeringWheelSimpleDlg::OnBnClickedCheckCarAirborne)
	ON_BN_CLICKED(IDC_CHECKSOFTSTOP, &CSteeringWheelSimpleDlg::OnBnClickedCheckSoftstop)
    ON_BN_CLICKED(IDC_CHECKFULLRANGE, &CSteeringWheelSimpleDlg::OnBnClickedCheckFullRange)
    ON_BN_CLICKED(IDC_LogiGetGUID, &CSteeringWheelSimpleDlg::OnBnClickedLogiGetDevicePath)
END_MESSAGE_MAP()


// CSteeringWheelSimpleDlg message handlers

BOOL CSteeringWheelSimpleDlg::OnInitDialog()
{
	CDialogEx::OnInitDialog();

	// Set the icon for this dialog.  The framework does this automatically
	//  when the application's main window is not a dialog
	SetIcon(m_hIcon, TRUE);			// Set big icon
	SetIcon(m_hIcon, FALSE);		// Set small icon

	m_deviceIndex = 0;
	m_offsetPercentage = 0;
	m_saturationPercentage = 0;
	m_coefficientPercentage = 0;
	m_magnitudePercentage = 0;
	m_usableRangePercentage = 0;

	m_allDevices = false;
	m_spring = false;
	m_constant = false;
	m_damper = false;
	m_dirtRoad = false;
	m_bumpyRoad = false;
	m_slipperyRoad = false;
	m_carAirborne = false;
	m_softstop = false;

	SetDlgItemInt(IDC_DEVICE, m_deviceIndex, 0);

	m_scrollBarOffset.SetScrollRange(0, 100);
	m_scrollBarSaturation.SetScrollRange(0, 100);
	m_scrollBarCoefficient.SetScrollRange(0, 100);
	m_scrollBarMagnitude.SetScrollRange(0, 100);
	m_scrollBarUsableRange.SetScrollRange(0, 100);

	SetTimer(1, 1000 / 30, NULL);

	return TRUE;  // return TRUE  unless you set the focus to a control
}

// If you add a minimize button to your dialog, you will need the code below
//  to draw the icon.  For MFC applications using the document/view model,
//  this is automatically done for you by the framework.

void CSteeringWheelSimpleDlg::OnPaint()
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
		CDialogEx::OnPaint();
	}
}

void CSteeringWheelSimpleDlg::OnTimer(UINT_PTR nIDEvent)
{
	UNREFERENCED_PARAMETER(nIDEvent);

	// Update the input device every timer message.

	//if the return value is false, means that the application has not been initialized yet and there is no hwnd available
	if (!LogiUpdate()) return;

	if (m_spring)
	{
		if (m_allDevices)
		{
			for (int t_index = 0; LogiIsConnected(t_index); t_index++)
			{
				LogiPlaySpringForce(t_index, m_offsetPercentage, m_saturationPercentage, m_coefficientPercentage);
			}
		}
		else
		{
			LogiPlaySpringForce(m_deviceIndex, m_offsetPercentage, m_saturationPercentage, m_coefficientPercentage);
		}
	}
	if (m_constant)
	{
		if (m_allDevices)
		{
			for (int t_index = 0; LogiIsConnected(t_index); t_index++)
			{
				LogiPlayConstantForce(t_index, m_magnitudePercentage);
			}
		}
		else
		{
			LogiPlayConstantForce(m_deviceIndex, m_magnitudePercentage);
		}
	}
	if (m_damper)
	{
		if (m_allDevices)
		{
			for (int t_index = 0; LogiIsConnected(t_index); t_index++)
			{
				LogiPlayDamperForce(t_index, m_coefficientPercentage);
			}
		}
		else
		{
			LogiPlayDamperForce(m_deviceIndex, m_coefficientPercentage);
		}
	}
	if (m_dirtRoad)
	{
		if (m_allDevices)
		{
			for (int t_index = 0; LogiIsConnected(t_index); t_index++)
			{
				LogiPlayDirtRoadEffect(t_index, m_magnitudePercentage);
			}
		}
		else
		{
			LogiPlayDirtRoadEffect(m_deviceIndex, m_magnitudePercentage);
		}
	}
	if (m_bumpyRoad)
	{
		if (m_allDevices)
		{
			for (int t_index = 0; LogiIsConnected(t_index); t_index++)
			{
				LogiPlayBumpyRoadEffect(t_index, m_magnitudePercentage);
			}
		}
		else
		{
			LogiPlayBumpyRoadEffect(m_deviceIndex, m_magnitudePercentage);
		}
	}
	if (m_slipperyRoad)
	{
		if (m_allDevices)
		{
			for (int t_index = 0; LogiIsConnected(t_index); t_index++)
			{
				LogiPlaySlipperyRoadEffect(t_index, m_magnitudePercentage);
			}
		}
		else
		{
			LogiPlaySlipperyRoadEffect(m_deviceIndex, m_magnitudePercentage);
		}
	}
	if (m_carAirborne)
	{
		if (m_allDevices)
		{
			for (int t_index = 0; LogiIsConnected(t_index); t_index++)
			{
				LogiPlayCarAirborne(t_index);
			}
		}
		else
		{
			LogiPlayCarAirborne(m_deviceIndex);
		}
	}
	if (m_softstop)
	{
		if (m_allDevices)
		{
			for (int t_index = 0; LogiIsConnected(t_index); t_index++)
			{
				LogiPlaySoftstopForce(t_index, m_usableRangePercentage);
			}
		}
		else
		{
			LogiPlaySoftstopForce(m_deviceIndex, m_usableRangePercentage);
		}
	}
}


void CSteeringWheelSimpleDlg::OnHScroll(UINT nSBCode, UINT nPos, CScrollBar* pScrollBar)
{
	int CurPos = pScrollBar->GetScrollPos();

	// Determine the new position of scroll box.
	switch (nSBCode)
	{
	case SB_LEFT:      // Scroll to far left.
		CurPos = SCROLL_MIN;
		break;

	case SB_RIGHT:      // Scroll to far right.
		CurPos = SCROLL_MAX;
		break;

	case SB_ENDSCROLL:   // End scroll.
		break;

	case SB_LINELEFT:      // Scroll left.
		if (CurPos > SCROLL_MIN)
			CurPos--;
		break;

	case SB_LINERIGHT:   // Scroll right.
		if (CurPos < SCROLL_MAX)
			CurPos++;
		break;

	case SB_PAGELEFT:    // Scroll one page left.
	{
		// Get the page size. 
		SCROLLINFO   info;
		pScrollBar->GetScrollInfo(&info, SIF_ALL);

		if (CurPos > SCROLL_MIN)
			CurPos = max(0, CurPos - (int)info.nPage);
	}
	break;

	case SB_PAGERIGHT:      // Scroll one page right
	{
		// Get the page size. 
		SCROLLINFO   info;
		pScrollBar->GetScrollInfo(&info, SIF_ALL);

		if (CurPos < SCROLL_MAX)
			CurPos = min(122, CurPos + (int)info.nPage);
	}
	break;

	case SB_THUMBPOSITION: // Scroll to absolute position. nPos is the position
		CurPos = nPos;      // of the scroll box at the end of the drag operation.
		break;

	case SB_THUMBTRACK:   // Drag scroll box to specified position. nPos is the
		CurPos = nPos;     // position that the scroll box has been dragged to.
		break;
	}

	pScrollBar->SetScrollPos(CurPos);

	m_offsetPercentage = m_scrollBarOffset.GetScrollPos();
	m_saturationPercentage = m_scrollBarSaturation.GetScrollPos();
	m_coefficientPercentage = m_scrollBarCoefficient.GetScrollPos();
	m_magnitudePercentage = m_scrollBarMagnitude.GetScrollPos();
	m_usableRangePercentage = m_scrollBarUsableRange.GetScrollPos();

	if (m_fullRange)
	{
		m_offsetPercentage = (m_offsetPercentage * 2) - 100;
		m_coefficientPercentage = (m_coefficientPercentage * 2) - 100;
		m_magnitudePercentage = (m_magnitudePercentage * 2) - 100;
	}

	CDialogEx::OnHScroll(nSBCode, nPos, pScrollBar);
}

// The system calls this function to obtain the cursor to display while the user drags
//  the minimized window.
HCURSOR CSteeringWheelSimpleDlg::OnQueryDragIcon()
{
	return static_cast<HCURSOR>(m_hIcon);
}

void CSteeringWheelSimpleDlg::OnBnClickedExit()
{
	CDialog::OnOK();
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiSteeringInitialize()
{
	if (LogiSteeringInitialize(true))
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiSteeringInitialize returned TRUE");
	}
	else
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiSteeringInitialize returned FALSE");
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiSteeringInitializeWithWindow()
{
	if (LogiSteeringInitializeWithWindow(true, m_hWnd))
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiSteeringInitializeWithWindow returned TRUE");
	}
	else
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiSteeringInitializeWithWindow returned FALSE");
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiSteeringGetSdkVersion()
{
	// TODO: Add your control notification handler code here
	::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiSteeringGetSdkVersion is not yet implemented");
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiUpdate()
{
	if (LogiUpdate())
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiUpdate returned TRUE");
	}
	else
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiUpdate returned FALSE");
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiGetStateENGINES()
{
	// TODO: Add your control notification handler code here
	::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiGetStateENGINES is not yet implemented");
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiGetState()
{
	// TODO: Add your control notification handler code here
	::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiGetState is not yet implemented");
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiGetFriendlyProductName()
{
    TCHAR sBuffer_[512] = { '\0' };
    TCHAR buf[128] = { '\0' };

    if (LogiIsConnected(m_deviceIndex) && LogiGetFriendlyProductName(m_deviceIndex, buf, 128))
    {
        wsprintf(sBuffer_, TEXT("%s %s"), L"LogiGetFriendlyProductName returned", buf);
        ::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), sBuffer_);
    }
    else
    {
        ::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiGetFriendlyProductName returned FALSE");
    }
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiGetDevicePath()
{
    TCHAR sBuffer_[512] = { '\0' };
    wchar_t buf[MAX_PATH] = { '\0' };

    if (LogiIsConnected(m_deviceIndex) && LogiGetDevicePath(m_deviceIndex, buf, MAX_PATH))
    {
        wsprintf(sBuffer_, TEXT("%s %s"), L"LogiGetDevicePath returned", buf);
        ::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), sBuffer_);
    }
    else
    {
        ::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiGetDevicePath returned FALSE");
    }
}

void CSteeringWheelSimpleDlg::OnBnClickedLogiIsConnected()
{
	if (LogiIsConnected(m_deviceIndex))
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiIsConnected is TRUE");
	}
	else
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiIsConnected is FALSE");
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiIsDeviceConnected()
{
	if (LogiIsDeviceConnected(m_deviceIndex, LOGI_DEVICE_TYPE_WHEEL))
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiIsDeviceConnected is TRUE");
	}
	else
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiIsDeviceConnected is FALSE");
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiIsManufacturerConnected()
{
	if (LogiIsManufacturerConnected(m_deviceIndex, LOGI_MANUFACTURER_LOGITECH))
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiIsManufacturerConnected is TRUE");
	}
	else
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiIsManufacturerConnected is FALSE");
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiIsModelConnected()
{
	if (LogiIsModelConnected(m_deviceIndex, LOGI_MODEL_G29))
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiIsModelConnected is TRUE");
	}
	else
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiIsModelConnected is FALSE");
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiButtonTriggered()
{
	if (LogiButtonTriggered(m_deviceIndex, 0))
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiButtonTriggered returned TRUE");
	}
	else
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiButtonTriggered returned FALSE");
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiButtonReleased()
{
	if (LogiButtonReleased(m_deviceIndex, 0))
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiButtonReleased returned TRUE");
	}
	else
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiButtonReleased returned FALSE");
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiButtonIsPressed()
{
	if (LogiButtonIsPressed(m_deviceIndex, 0))
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiButtonIsPressed returned TRUE");
	}
	else
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiButtonIsPressed returned FALSE");
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiGenerateNonLinearValues()
{
	// TODO: Add your control notification handler code here
	::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiGenerateNonLinearValues is not yet implemented");
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiGetNonLinearValue()
{
	// TODO: Add your control notification handler code here
	::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiGetNonLinearValue is not yet implemented");
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiHasForceFeedback()
{
	if (LogiHasForceFeedback(m_deviceIndex))
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiHasForceFeedback returned TRUE");
	}
	else
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiHasForceFeedback returned FALSE");
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiIsPlaying()
{
	if (LogiIsPlaying(m_deviceIndex, 0))
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiIsPlaying returned TRUE");
	}
	else
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiIsPlaying returned FALSE");
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiPlaySpringForce()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiPlaySpringForce(t_index, m_offsetPercentage, m_saturationPercentage, m_coefficientPercentage);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlaySpringForce is now active on all devices");
	}
	else
	{
		if (LogiPlaySpringForce(m_deviceIndex, m_offsetPercentage, m_saturationPercentage, m_coefficientPercentage))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlaySpringForce returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlaySpringForce returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiStopSpringForce()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiStopSpringForce(t_index);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopSpringForce is now active on all devices");
	}
	else
	{
		if (LogiStopSpringForce(m_deviceIndex))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopSpringForce returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopSpringForce returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiPlayConstantForce()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiPlayConstantForce(t_index, m_magnitudePercentage);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayConstantForce is now active on all devices");
	}
	else
	{
		if (LogiPlayConstantForce(m_deviceIndex, m_magnitudePercentage))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayConstantForce returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayConstantForce returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiStopConstantForce()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiStopConstantForce(t_index);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopConstantForce is now active on all devices");
	}
	else
	{
		if (LogiPlayConstantForce(m_deviceIndex, m_magnitudePercentage))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopConstantForce returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopConstantForce returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiPlayDamperForce()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiPlayDamperForce(t_index, m_coefficientPercentage);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayDamperForce is now active on all devices");
	}
	else
	{
		if (LogiPlayDamperForce(m_deviceIndex, m_coefficientPercentage))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayDamperForce returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayDamperForce returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiStopDamperForce()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiStopDamperForce(t_index);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopDamperForce is now active on all devices");
	}
	else
	{
		if (LogiStopDamperForce(m_deviceIndex))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopDamperForce returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopDamperForce returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiPlaySideCollisionForce()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiPlaySideCollisionForce(t_index, m_magnitudePercentage);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlaySideCollisionForce is now active on all devices");
	}
	else
	{
		if (LogiPlaySideCollisionForce(m_deviceIndex, m_magnitudePercentage))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlaySideCollisionForce returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlaySideCollisionForce returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiPlayFrontalCollisionForce()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiPlayFrontalCollisionForce(t_index, m_magnitudePercentage);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayFrontalCollisionForce is now active on all devices");
	}
	else
	{
		if (LogiPlayFrontalCollisionForce(m_deviceIndex, m_magnitudePercentage))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayFrontalCollisionForce returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayFrontalCollisionForce returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiPlayDirtRoadEffect()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiPlayDirtRoadEffect(t_index, m_magnitudePercentage);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayDirtRoadEffect is now active on all devices");
	}
	else
	{
		if (LogiPlayDirtRoadEffect(m_deviceIndex, m_magnitudePercentage))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayDirtRoadEffect returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayDirtRoadEffect returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiStopDirtRoadEffect()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiStopDirtRoadEffect(t_index);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopDirtRoadEffect is now active on all devices");
	}
	else
	{
		if (LogiStopDirtRoadEffect(m_deviceIndex))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopDirtRoadEffect returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopDirtRoadEffect returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiPlayBumpyRoadEffect()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiPlayBumpyRoadEffect(t_index, m_magnitudePercentage);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayBumpyRoadEffect is now active on all devices");
	}
	else
	{
		if (LogiPlayBumpyRoadEffect(m_deviceIndex, m_magnitudePercentage))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayBumpyRoadEffect returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayBumpyRoadEffect returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiStopBumpyRoadEffect()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiStopBumpyRoadEffect(t_index);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopBumpyRoadEffect is now active on all devices");
	}
	else
	{
		if (LogiStopBumpyRoadEffect(m_deviceIndex))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopBumpyRoadEffect returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopBumpyRoadEffect returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiPlaySlipperyRoadEffect()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiPlaySlipperyRoadEffect(t_index, m_magnitudePercentage);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlaySlipperyRoadEffect is now active on all devices");
	}
	else
	{
		if (LogiPlaySlipperyRoadEffect(m_deviceIndex, m_magnitudePercentage))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlaySlipperyRoadEffect returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlaySlipperyRoadEffect returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiStopSlipperyRoadEffect()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiStopSlipperyRoadEffect(t_index);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopSlipperyRoadEffect is now active on all devices");
	}
	else
	{
		if (LogiStopSlipperyRoadEffect(m_deviceIndex))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopSlipperyRoadEffect returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopSlipperyRoadEffect returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiPlaySurfaceEffect()
{
	if (LogiPlaySurfaceEffect(m_deviceIndex, 0, m_magnitudePercentage, 100))
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlaySurfaceEffect returned TRUE");
	}
	else
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlaySurfaceEffect returned FALSE");
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiStopSurfaceEffect()
{
	if (LogiStopSurfaceEffect(m_deviceIndex))
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopSurfaceEffect returned TRUE");
	}
	else
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopSurfaceEffect returned FALSE");
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiPlayCarAirborne()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiPlayCarAirborne(t_index);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayCarAirborne is now active on all devices");
	}
	else
	{
		if (LogiPlayCarAirborne(m_deviceIndex))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayCarAirborne returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayCarAirborne returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiStopCarAirborne()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiStopCarAirborne(t_index);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopCarAirborne is now active on all devices");
	}
	else
	{
		if (LogiStopCarAirborne(m_deviceIndex))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopCarAirborne returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopCarAirborne returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiPlaySoftstopForce()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiPlaySoftstopForce(t_index, m_usableRangePercentage);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlaySoftstopForce is now active on all devices");
	}
	else
	{
		if (LogiPlaySoftstopForce(m_deviceIndex, m_usableRangePercentage))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlaySoftstopForce returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlaySoftstopForce returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiStopSoftstopForce()
{
	if (m_allDevices)
	{
		for (int t_index = 0; LogiIsConnected(t_index); t_index++)
		{
			LogiStopSoftstopForce(t_index);
		}
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopSoftstopForce is now active on all devices");
	}
	else
	{
		if (LogiStopSoftstopForce(m_deviceIndex))
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopSoftstopForce returned TRUE");
		}
		else
		{
			::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopSoftstopForce returned FALSE");
		}
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiSetPreferredControllerProperties()
{
	// TODO: Add your control notification handler code here
    if (m_allDevices)
    {

        for (int t_index = 0; LogiIsConnected(t_index); t_index++)
        {
            LogiControllerPropertiesData data;
            ZeroMemory(&data, sizeof(data));

            LogiGetCurrentControllerProperties(t_index, data);

            data.wheelRange = 900;

            LogiSetPreferredControllerProperties(data);
        }
        ::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopSoftstopForce is now active on all devices");
    }
    else
    {
        LogiControllerPropertiesData data;
        ZeroMemory(&data, sizeof(data));

        LogiGetCurrentControllerProperties(m_deviceIndex, data);

        data.wheelRange = 900;

        LogiSetPreferredControllerProperties(data);
    }
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiGetCurrentControllerProperties()
{
    if (m_allDevices)
    {

        for (int t_index = 0; LogiIsConnected(t_index); t_index++)
        {
            LogiControllerPropertiesData data;
            ZeroMemory(&data, sizeof(data));

            LogiGetCurrentControllerProperties(t_index, data);

            data.wheelRange = 200;

            LogiSetPreferredControllerProperties(data);
        }
        ::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiStopSoftstopForce is now active on all devices");
    }
    else
    {
        LogiControllerPropertiesData data;
        ZeroMemory(&data, sizeof(data));

        LogiGetCurrentControllerProperties(m_deviceIndex, data);

        data.wheelRange = 200;

        LogiSetPreferredControllerProperties(data);
    }
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiGetShifterMode()
{
	TCHAR sBuffer_[512] = { '\0' };

	wsprintf(sBuffer_, TEXT("%s %d"), L"LogiGetShifterMode returned", LogiGetShifterMode(m_deviceIndex));
	::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), sBuffer_);
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiPlayLeds()
{
	if (LogiPlayLeds(m_deviceIndex, 1, 1, 1))
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayLeds returned TRUE");
	}
	else
	{
		::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiPlayLeds returned FALSE");
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedLogiSteeringShutdown()
{
	LogiSteeringShutdown();
	::SetWindowText(::GetDlgItem(m_hWnd, IDC_RESULT), L"LogiSteeringShutdown has no return");
}


void CSteeringWheelSimpleDlg::OnEnChangeDevice()
{
	m_deviceIndex = GetDlgItemInt(IDC_DEVICE, 0, 0);
}


void CSteeringWheelSimpleDlg::OnBnClickedCheckAllDevices()
{
	CWnd *wndDevice = this->GetDlgItem(IDC_DEVICE);

	if (m_allDevices)
	{
		m_allDevices = false;
		wndDevice->EnableWindow(true);
	}
	else
	{
		m_allDevices = true;
		wndDevice->EnableWindow(false);
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedCheckFullRange()
{
	if (m_fullRange)
	{
		m_fullRange = false;
	}
	else
	{
		m_fullRange = true;
	}
}



void CSteeringWheelSimpleDlg::OnBnClickedCheckSpring()
{
	LogiSteeringInitialize(true);
	if (m_spring)
	{
		m_spring = false;
	}
	else
	{
		m_spring = true;
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedCheckConstant()
{
	LogiSteeringInitialize(true);
	if (m_constant)
	{
		m_constant = false;
	}
	else
	{
		m_constant = true;
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedCheckDamper()
{
	LogiSteeringInitialize(true);
	if (m_damper)
	{
		m_damper = false;
	}
	else
	{
		m_damper = true;
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedCheckDirtRoad()
{
	LogiSteeringInitialize(true);
	if (m_dirtRoad)
	{
		m_dirtRoad = false;
	}
	else
	{
		m_dirtRoad = true;
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedCheckBumpyRoad()
{
	LogiSteeringInitialize(true);
	if (m_bumpyRoad)
	{
		m_bumpyRoad = false;
	}
	else
	{
		m_bumpyRoad = true;
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedCheckSlipperyRoad()
{
	LogiSteeringInitialize(true);
	if (m_slipperyRoad)
	{
		m_slipperyRoad = false;
	}
	else
	{
		m_slipperyRoad = true;
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedCheckCarAirborne()
{
	LogiSteeringInitialize(true);
	if (m_carAirborne)
	{
		m_carAirborne = false;
	}
	else
	{
		m_carAirborne = true;
	}
}


void CSteeringWheelSimpleDlg::OnBnClickedCheckSoftstop()
{
	LogiSteeringInitialize(true);
	if (m_softstop)
	{
		m_softstop = false;
	}
	else
	{
		m_softstop = true;
	}
}

