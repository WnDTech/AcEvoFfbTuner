#pragma once

#include "LogitechSteeringWheelLib.h"
#pragma comment(lib, "LogitechSteeringWheelLib.lib")

#include "map"

// CSteeringWheelInputCalibrateDlg dialog

class CSteeringWheelInputCalibrateDlg : public CDialogEx
{
	DECLARE_DYNAMIC(CSteeringWheelInputCalibrateDlg)

public:
	CSteeringWheelInputCalibrateDlg(int deviceIndex, HWND hParent, CWnd* pParent = NULL);   // standard constructor
	virtual ~CSteeringWheelInputCalibrateDlg();

// Dialog Data
	enum { IDD = IDD_INPUTCALIBRATE_DIALOG };

	int deviceIndex;
	HWND hParent;

	virtual BOOL OnInitDialog();
	afx_msg void OnTimer(UINT_PTR nIDEvent);

	bool CSteeringWheelInputCalibrateDlg::CheckInstruction();
	void CSteeringWheelInputCalibrateDlg::NextInstruction();

protected:
	virtual void DoDataExchange(CDataExchange* pDX);    // DDX/DDV support

	DECLARE_MESSAGE_MAP()
public:
	int m_calibrationAction;
	int m_controlCurrent;
	DIJOYSTATE2 *m_lastDIJoyState2;
	CString m_controlChanges[4];
	std::map<int, CString> m_controlMessages;
	std::map<int, CString> m_controlStatus;
	std::map<int, CString> m_controlSteps;
	std::map<int, CString> m_controlNames;

	afx_msg void OnBnClickedSkip();
	afx_msg void OnBnClickedNext();
	afx_msg void OnBnClickedAccept();
	afx_msg void OnBnClickedCancel();

	void UpdateText();
	void ShowAccept();
	void ExitWindow();
};
