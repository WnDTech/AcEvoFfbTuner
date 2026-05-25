#include "stdafx.h"

#include "LCoreMessages.h"
#include "LogiIndependant.h"
#include "crtdbg.h"

using namespace LogitechSteeringWheel;

namespace LogitechSteeringWheel
{
	HRESULT SetOperatingRangeDInput(CONST LPDIRECTINPUTDEVICE8 deviceHandle, CONST INT range)
	{
		if (NULL == deviceHandle)
		{
			return E_POINTER;
		}

		DIPROPDWORD dipdw;  // DIPROPDWORD contains a DIPROPHEADER structure. 
		HRESULT hr;
		LRESULT lr;

		dipdw.diph.dwSize = sizeof(DIPROPDWORD);
		dipdw.diph.dwHeaderSize = sizeof(DIPROPHEADER);
		dipdw.diph.dwObj = 0; // device property 
		dipdw.diph.dwHow = DIPH_DEVICE;

		hr = deviceHandle->GetProperty(DIPROP_VIDPID, &dipdw.diph);
		if (SUCCEEDED(hr))
		{
			int wProductID = HIWORD(dipdw.dwData);
			const wchar_t* msgName = LGS_Msg_SetOperatingRange;

			UINT msgId = RegisterWindowMessage(msgName);
			if (0 == msgId)
			{
				return false;
			}

			// Get the window handler
			HWND hLGSMsgHandler = FindWindow(LCoreMessagesClassName, NULL);
			if (NULL == hLGSMsgHandler)
			{
				return false;
			}

			lr = SendMessage(hLGSMsgHandler, msgId, wProductID, range);
			if (lr == 0)
			{
				return E_FAIL;
			}
		}

		return hr;
	}

	HRESULT GetOperatingRangeDInput(CONST LPDIRECTINPUTDEVICE8 deviceHandle, INT& range)
	{
		if (NULL == deviceHandle)
		{
			return E_POINTER;
		}

		DIPROPDWORD dipdw;  // DIPROPDWORD contains a DIPROPHEADER structure. 
		HRESULT hr;

		dipdw.diph.dwSize = sizeof(DIPROPDWORD);
		dipdw.diph.dwHeaderSize = sizeof(DIPROPHEADER);
		dipdw.diph.dwObj = 0; // device property 
		dipdw.diph.dwHow = DIPH_DEVICE;

		hr = deviceHandle->GetProperty(DIPROP_VIDPID, &dipdw.diph);
		if (SUCCEEDED(hr))
		{
			int wProductID = HIWORD(dipdw.dwData);
			const wchar_t* msgName = LGS_Msg_GetOperatingRange;

			UINT msgId = RegisterWindowMessage(msgName);
			if (0 == msgId)
			{
				return false;
			}

			// Get the window handler
			HWND hLGSMsgHandler = FindWindow(LCoreMessagesClassName, NULL);
			if (NULL == hLGSMsgHandler)
			{
				return false;
			}

			range = SendMessage(hLGSMsgHandler, msgId, wProductID, 0);
		}

		return hr;
	}

	HRESULT PlayLedsDInput(CONST LPDIRECTINPUTDEVICE8 deviceHandle, CONST FLOAT currentRPM, CONST FLOAT rpmFirstLedTurnsOn, CONST FLOAT rpmRedLine)
	{
		if (NULL == deviceHandle)
		{
			return E_POINTER;
		}

		WheelData wheelData_;
		ZeroMemory(&wheelData_, sizeof(wheelData_));

		wheelData_.size = sizeof(WheelData);
		wheelData_.versionNbr = LEDS_VERSION_NUMBER;
		wheelData_.rpmData.currentRPM = currentRPM;
		wheelData_.rpmData.rpmFirstLedTurnsOn = rpmFirstLedTurnsOn;
		wheelData_.rpmData.rpmRedLine = rpmRedLine;

		DIEFFESCAPE data_;
		ZeroMemory(&data_, sizeof(data_));

		data_.dwSize = sizeof(DIEFFESCAPE);
		data_.dwCommand = ESCAPE_COMMAND_LEDS;
		data_.lpvInBuffer = &wheelData_;
		data_.cbInBuffer = sizeof(wheelData_);

		HRESULT hr;
		hr = deviceHandle->Escape(&data_);

		return hr;
	}
}