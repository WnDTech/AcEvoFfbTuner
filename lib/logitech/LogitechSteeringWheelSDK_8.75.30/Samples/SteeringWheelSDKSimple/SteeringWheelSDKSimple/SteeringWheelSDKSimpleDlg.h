
// DemoWheelSimpleDlg.h : header file
//

#pragma once

enum {
	SCROLL_MIN = 0,
	SCROLL_MAX = 100
};

// CSteeringWheelSimpleDlg dialog
class CSteeringWheelSimpleDlg : public CDialogEx
{
// Construction
public:
	CSteeringWheelSimpleDlg(CWnd* pParent = NULL);	// standard constructor

// Dialog Data
	enum { IDD = IDD_DEMOWHEELSIMPLE_DIALOG };

	protected:
	virtual void DoDataExchange(CDataExchange* pDX);	// DDX/DDV support


// Implementation
protected:
	HICON m_hIcon;

	// Generated message map functions
	virtual BOOL OnInitDialog();
	afx_msg void OnPaint();
	afx_msg HCURSOR OnQueryDragIcon();
	afx_msg void OnTimer(UINT_PTR nIDEvent);
	afx_msg void OnHScroll(UINT nSBCode, UINT nPos, CScrollBar* pScrollBar);
	DECLARE_MESSAGE_MAP()
public:
	int m_deviceIndex;
	int m_offsetPercentage;
	int m_saturationPercentage;
	int m_coefficientPercentage;
	int m_magnitudePercentage;
	int m_usableRangePercentage;
	bool m_allDevices;
	bool m_fullRange;
	bool m_spring;
	bool m_constant;
	bool m_damper;
	bool m_dirtRoad;
	bool m_bumpyRoad;
	bool m_slipperyRoad;
	bool m_carAirborne;
	bool m_softstop;

	CScrollBar m_scrollBarOffset;
	CScrollBar m_scrollBarSaturation;
	CScrollBar m_scrollBarCoefficient;
	CScrollBar m_scrollBarMagnitude;
	CScrollBar m_scrollBarUsableRange;
	CButton m_checkAllDevices;
	CButton m_checkFullRangePercentages;
	CButton m_checkSpring;
	CButton m_checkConstant;
	CButton m_checkDamper;
	CButton m_checkDirtRoad;
	CButton m_checkBumpyRoad;
	CButton m_checkSlipperyRoad;
	CButton m_checkCarAirborne;
	CButton m_checkSoftstop;

	afx_msg void OnBnClickedExit();
	afx_msg void OnBnClickedIsconnected();
	afx_msg void OnBnClickedLogiSteeringInitialize();
	afx_msg void OnBnClickedLogiSteeringInitializeWithWindow();
	afx_msg void OnBnClickedLogiSteeringGetSdkVersion();
	afx_msg void OnBnClickedLogiUpdate();
	afx_msg void OnBnClickedLogiGetStateENGINES();
	afx_msg void OnBnClickedLogiGetState();
	afx_msg void OnBnClickedLogiGetFriendlyProductName();
	afx_msg void OnBnClickedLogiIsConnected();
	afx_msg void OnBnClickedLogiIsDeviceConnected();
	afx_msg void OnBnClickedLogiIsManufacturerConnected();
	afx_msg void OnBnClickedLogiIsModelConnected();
	afx_msg void OnBnClickedLogiButtonTriggered();
	afx_msg void OnBnClickedLogiButtonReleased();
	afx_msg void OnBnClickedLogiButtonIsPressed();
	afx_msg void OnBnClickedLogiGenerateNonLinearValues();
	afx_msg void OnBnClickedLogiGetNonLinearValue();
	afx_msg void OnBnClickedLogiHasForceFeedback();
	afx_msg void OnBnClickedLogiIsPlaying();
	afx_msg void OnBnClickedLogiPlaySpringForce();
	afx_msg void OnBnClickedLogiStopSpringForce();
	afx_msg void OnBnClickedLogiPlayConstantForce();
	afx_msg void OnBnClickedLogiStopConstantForce();
	afx_msg void OnBnClickedLogiPlayDamperForce();
	afx_msg void OnBnClickedLogiStopDamperForce();
	afx_msg void OnBnClickedLogiPlaySideCollisionForce();
	afx_msg void OnBnClickedLogiPlayFrontalCollisionForce();
	afx_msg void OnBnClickedLogiPlayDirtRoadEffect();
	afx_msg void OnBnClickedLogiStopDirtRoadEffect();
	afx_msg void OnBnClickedLogiPlayBumpyRoadEffect();
	afx_msg void OnBnClickedLogiStopBumpyRoadEffect();
	afx_msg void OnBnClickedLogiPlaySlipperyRoadEffect();
	afx_msg void OnBnClickedLogiStopSlipperyRoadEffect();
	afx_msg void OnBnClickedLogiPlaySurfaceEffect();
	afx_msg void OnBnClickedLogiStopSurfaceEffect();
	afx_msg void OnBnClickedLogiPlayCarAirborne();
	afx_msg void OnBnClickedLogiStopCarAirborne();
	afx_msg void OnBnClickedLogiPlaySoftstopForce();
	afx_msg void OnBnClickedLogiStopSoftstopForce();
	afx_msg void OnBnClickedLogiSetPreferredControllerProperties();
	afx_msg void OnBnClickedLogiGetCurrentControllerProperties();
	afx_msg void OnBnClickedLogiGetShifterMode();
	afx_msg void OnBnClickedLogiPlayLeds();
	afx_msg void OnBnClickedLogiSteeringShutdown();
	afx_msg void OnEnChangeDevice();
	afx_msg void OnBnClickedCheckAllDevices();
	afx_msg void OnBnClickedCheckSpring();
	afx_msg void OnBnClickedCheckConstant();
	afx_msg void OnBnClickedCheckDamper();
	afx_msg void OnBnClickedCheckDirtRoad();
	afx_msg void OnBnClickedCheckBumpyRoad();
	afx_msg void OnBnClickedCheckSlipperyRoad();
	afx_msg void OnBnClickedCheckCarAirborne();
	afx_msg void OnBnClickedCheckSoftstop();
	afx_msg void OnBnClickedCheckFullRange();
    afx_msg void OnBnClickedLogiGetDevicePath();
};
