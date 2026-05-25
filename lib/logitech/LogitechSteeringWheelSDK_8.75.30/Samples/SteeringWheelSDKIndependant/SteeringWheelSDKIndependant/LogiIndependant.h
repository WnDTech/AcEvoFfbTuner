#ifndef LOGI_INDEPENDANT_H_INCLUDED_
#define LOGI_INDEPENDANT_H_INCLUDED_

#define DIRECTINPUT_VERSION 0x0800
#include <dinput.h>

namespace LogitechSteeringWheel
{
	CONST DWORD ESCAPE_COMMAND_LEDS = 0;
	CONST DWORD LEDS_VERSION_NUMBER = 0x00000001;

	struct LedsRpmData
	{
		FLOAT currentRPM;
		FLOAT rpmFirstLedTurnsOn;
		FLOAT rpmRedLine;
	};

	struct WheelData
	{
		DWORD size;
		DWORD versionNbr;
		LedsRpmData rpmData;
	};

	HRESULT SetOperatingRangeDInput(CONST LPDIRECTINPUTDEVICE8 deviceHandle, CONST INT range);
	HRESULT GetOperatingRangeDInput(CONST LPDIRECTINPUTDEVICE8 deviceHandle, INT& range);
	HRESULT PlayLedsDInput(CONST LPDIRECTINPUTDEVICE8 device, CONST FLOAT currentRPM, CONST FLOAT rpmFirstLedTurnsOn, CONST FLOAT rpmRedLine);
}

#endif // LOGI_INDEPENDANT_H_INCLUDED_